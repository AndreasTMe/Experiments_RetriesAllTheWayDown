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

mkdirSync(dataDir, { recursive: true });
writeFileSync(join(dataDir, "dead-letters-vs-outage.json"), JSON.stringify(exportDeadLettersVsOutage(), null, 4) + "\n");
