// Exports the data behind the blog post's charts from results/ to charts/components/data/, so the folder copies across as one.
// Run from anywhere with: node charts/export-data.mjs
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const chartsDir = dirname(fileURLToPath(import.meta.url));
const resultsDir = join(chartsDir, "..", "results");
const dataDir = join(chartsDir, "components", "data");

const outages = [
    { label: "30 s", immediate: "A-postgres-down-30s-20261027-175740", delayed: "A-postgres-down-30s-cfg6-20261028-224838" },
    { label: "2 min", immediate: "A-postgres-down-2min-20261026-230331", delayed: "A-postgres-down-2min-cfg6-20261028-223817" },
    { label: "5 min", immediate: "A-postgres-down-5min-20261027-175925", delayed: "A-postgres-down-5min-cfg6-20261028-225023" },
    { label: "10 min", immediate: "A-postgres-down-10min-20261027-180620", delayed: "A-postgres-down-10min-cfg6-20261028-225718" },
    { label: "20 min", immediate: "A-postgres-down-20min-20261027-181915", delayed: "A-postgres-down-20min-cfg6-20261028-231015" },
];

const faults = [
    {
        fault: "Fault A: Postgres down for 2 minutes",
        defaults: "A-postgres-down-2min-20261026-230331",
        fixed: "A-postgres-down-2min-cfg346-20261030-144830",
    },
    {
        fault: "Fault B: Postgres 15 s slower for 2 minutes",
        defaults: "B-postgres-slow-15s-20261026-231055",
        fixed: "B-postgres-slow-15s-cfg346-20261030-145153",
    },
];

const layers = [
    { key: "serviceBusDeliveries", label: "Deliveries" },
    { key: "httpAttemptsFromWorker", label: "HTTP attempts" },
    { key: "dbConnectionOpens", label: "DB opens" },
    { key: "dbCommands", label: "DB commands" },
    { key: "rows", label: "Rows" },
];

const brownout = {
    defaults: { charted: "D-brownout-cloud-20261031-222044", other: "D-brownout-cloud-20261031-171829" },
    fixed: { charted: "D-brownout-cfg346-cloud-20261031-220612", other: "D-brownout-cfg346-cloud-20261031-172815" },
};

const sampleEverySeconds = 5;

function readRun(run) {
    const read = (file) => JSON.parse(readFileSync(join(resultsDir, run, file), "utf8"));
    return { summary: read("summary.json"), config: read("config.json") };
}

function deadLetters(run, expectDelayed) {
    const { summary, config } = readRun(run);
    if (!summary.drained) {
        throw new Error(`${run} did not drain`);
    }
    if (Boolean(config.worker.experiment.delayedRedelivery) !== expectDelayed) {
        throw new Error(`${run} has the wrong delayedRedelivery setting`);
    }
    return summary.deadLettered.fromPeekingDlq;
}

function exportDeadLettersVsOutage() {
    return {
        measuredOn: "Service Bus emulator",
        points: outages.map(({ label, immediate, delayed }) => ({
            outage: label,
            outageSeconds: readRun(immediate).summary.scenario.faultDurationSeconds,
            immediate: deadLetters(immediate, false),
            delayed: deadLetters(delayed, true),
            runs: { immediate, delayed },
        })),
    };
}

function layerCounts(run, expectFixed) {
    const { summary, config } = readRun(run);
    if (!summary.drained) {
        throw new Error(`${run} did not drain`);
    }
    const { passCancellation, tunedBreaker, delayedRedelivery, retryBudget } = config.worker.experiment;
    const isFixed = Boolean(passCancellation && tunedBreaker && delayedRedelivery);
    const isDefaults = !passCancellation && !tunedBreaker && !delayedRedelivery;
    if ((expectFixed ? !isFixed : !isDefaults) || retryBudget) {
        throw new Error(`${run} has the wrong experiment settings`);
    }
    return layers.map(({ key, label }) => ({
        layer: label,
        worst: summary.hitMessages.perLayer[key].max,
        average: summary.attemptsPerMessage[key].perMessage,
    }));
}

function exportAttemptsPerLayer() {
    return {
        measuredOn: "Service Bus emulator",
        faults: faults.map(({ fault, defaults, fixed }) => ({
            fault,
            defaults: layerCounts(defaults, false),
            fixed: layerCounts(fixed, true),
            runs: { defaults, fixed },
        })),
    };
}

function readTimeseries(run) {
    const [header, ...lines] = readFileSync(join(resultsDir, run, "timeseries.csv"), "utf8").trim().split(/\r?\n/);
    const columns = header.split(",");
    return lines.map((line) => {
        const values = line.split(",");
        const column = (name) => Number(values[columns.indexOf(name)]);
        return {
            seconds: Math.round(column("elapsed_s")),
            sent: column("producer.sent"),
            completed: column("worker.completed"),
            deadLettered: column("runner.dlq_peeked"),
        };
    });
}

function brownoutRun(run, expectFixed) {
    const { summary, config } = readRun(run);
    if (!summary.drained) {
        throw new Error(`${run} did not drain`);
    }
    if (config.runner.usesEmulator || config.worker.processor.maxConcurrentCalls !== 32) {
        throw new Error(`${run} isn't a real namespace run at MaxConcurrentCalls 32`);
    }
    const { passCancellation, tunedBreaker, delayedRedelivery, retryBudget } = config.worker.experiment;
    const isFixed = Boolean(passCancellation && tunedBreaker && delayedRedelivery);
    const isDefaults = !passCancellation && !tunedBreaker && !delayedRedelivery;
    if ((expectFixed ? !isFixed : !isDefaults) || retryBudget) {
        throw new Error(`${run} has the wrong experiment settings`);
    }
    return { summary };
}

function brownoutPanel({ charted, other }, expectFixed) {
    const { summary } = brownoutRun(charted, expectFixed);
    const samples = readTimeseries(charted);
    const last = samples[samples.length - 1];
    const { summary: otherSummary } = brownoutRun(other, expectFixed);
    return {
        run: charted,
        recoverySeconds: summary.recovery.recoverySeconds,
        samples: samples.filter((sample) => sample.seconds % sampleEverySeconds === 0 || sample === last),
        otherRun: {
            run: other,
            deadLettered: otherSummary.deadLettered.fromPeekingDlq,
            recoverySeconds: otherSummary.recovery.recoverySeconds,
        },
    };
}

function exportBrownoutMessages() {
    const { scenario } = readRun(brownout.defaults.charted).summary;
    return {
        measuredOn: "real Service Bus namespace",
        spike: { fromSeconds: 90, toSeconds: 120 },
        faultRemovedAtSeconds: scenario.faultRemovedAt,
        defaults: brownoutPanel(brownout.defaults, false),
        fixed: brownoutPanel(brownout.fixed, true),
    };
}

mkdirSync(dataDir, { recursive: true });
writeFileSync(join(dataDir, "attempts-per-layer.json"), JSON.stringify(exportAttemptsPerLayer(), null, 4) + "\n");
writeFileSync(join(dataDir, "dead-letters-vs-outage.json"), JSON.stringify(exportDeadLettersVsOutage(), null, 4) + "\n");
writeFileSync(join(dataDir, "brownout-messages.json"), JSON.stringify(exportBrownoutMessages(), null, 4) + "\n");
