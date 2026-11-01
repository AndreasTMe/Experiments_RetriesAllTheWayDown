# Retries all the way down

When a dependency fails, every layer between the user and that dependency retries on its own, and the retries multiply.
This repo measures that multiplication in a small, realistic .NET Aspire system: a producer puts orders on a Service Bus
queue, a worker takes each message and POSTs it to an API, and the API inserts a row into Postgres through EF Core. Each
layer runs with the defaults its library ships with: the Service Bus redelivery, the HttpClient standard resilience
handler, and the EF Core retrying execution strategy that Aspire turns on.

Out of the box it runs on those defaults, and the fixes tried against them are switches, off by default (see
[Configurations](#configurations)). Faults are injected between the API and Postgres with toxiproxy, or inside the API
with a fault middleware. Every layer counts what it does (deliveries, HTTP attempts, requests, connection opens,
commands, EF retries), and the database is counted directly, bypassing the proxy, so the numbers can be compared with
what actually landed. The values of every default in play are in [docs/defaults.md](docs/defaults.md).

```
Producer → Service Bus queue "orders" → Worker → HttpClient → OrdersApi → EF Core → toxiproxy → Postgres
```

## Prerequisites

- Windows 10 or later, or any OS that runs the tools below (only Windows 10 with Docker Desktop on WSL2 was tested)
- .NET SDK 10.0
- Docker Desktop, running
- About 4 GB of free memory for the containers (the Service Bus emulator needs SQL Server)

Nothing else: the Aspire AppHost SDK brings the Aspire CLI bundle and the dashboard. Packages come from nuget.org only
(see `nuget.config`).

## Layout

| Path                  | What                                                                                                         |
|-----------------------|--------------------------------------------------------------------------------------------------------------|
| `src/AppHost`         | Aspire AppHost: Service Bus emulator, Postgres, toxiproxy and the services                                   |
| `src/ServiceDefaults` | Aspire service defaults template, plus the `Experiment`, `Polly` and `Npgsql` meters and the shared counters |
| `src/Producer`        | Sends orders at a given rate: `POST /start?rate=&count=`, `POST /stop`                                       |
| `src/Worker`          | `ServiceBusProcessor` with SDK defaults; POSTs each message to OrdersApi                                     |
| `src/OrdersApi`       | `POST /orders` inserts a row with EF Core; fault middleware; ground truth                                    |
| `scripts/Runner`      | Console app that runs scenarios and the emulator check                                                       |
| `scripts/faults.ps1`  | Apply or remove a fault by hand                                                                              |
| `scenarios/`          | Scenario definitions                                                                                         |
| `docs/defaults.md`    | Every relevant default, with package, version and how it was checked                                         |
| `infra/`              | Bicep for the Standard namespace and the `orders` queue, for runs against a real namespace                   |
| `results/`            | The runs the post reports, one folder each                                                                   |
| `charts/`             | The script that exports chart data from `results/`, and the Astro chart components                           |
| `images/`             | The trace waterfall screenshot                                                                               |

## How to run

Start the system and leave it running:

```bash
dotnet run --project src/AppHost
```

The console prints the dashboard URL (https://localhost:17104 with a login token). Wait until every resource is Running.

Dashboard names:

| Name or prefix | Resources                                                                                                                                                          |
|----------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `external-`    | `external-servicebus` (queue, admin connection, SQL Server), `external-postgres` (database, pgweb UI), `external-toxiproxy` (the `ordersdb` connection through it) |
| `api-`         | `api-orders`                                                                                                                                                       |
| `worker-`      | `worker-orders`, `worker-producer`                                                                                                                                 |
| `test-runner`  | Runner commands: `scenario-<name>` for each scenario, `emulator-check` and the `faults-*` commands that take no arguments                                          |

The resources under `test-runner` are "Not started" on purpose. Start one with its Start button and read its console
log. Each one is a `dotnet run` of the Runner. Stopping it from the dashboard kills the process, so a scenario does not
clean up its faults: start `faults-clear` afterwards.

A `scenario-*` command and a `faults-*` command can use the same fault, but they are not the same thing.
`scenario-a-postgres-down-2min` is a whole measured run: it applies the fault on schedule, removes it, and writes the
results. `faults-postgres-down` only disables the proxy and exits, and Postgres stays down until `faults-postgres-up`
or `faults-clear`. Use the `faults-*` commands to try things by hand and to clean up. Only scenario runs produce
results.

Or, in a second terminal, run a scenario:

```bash
dotnet run --project scripts/Runner -- smoke
```

Other commands:

```bash
dotnet run --project scripts/Runner -- list
```

```bash
dotnet run --project scripts/Runner -- A-postgres-down-2min
```

A development or check run, under `results/dev/`, which git ignores:

```bash
dotnet run --project scripts/Runner -- A-postgres-down-2min --dev
```

A run of a fix: start the AppHost with that configuration's switches, then label the run with `--config`, so its folder
can't be mistaken for the baseline (`results/A-postgres-down-2min-cfg2a-{timestamp}/`):

```bash
dotnet run --project scripts/Runner -- A-postgres-down-2min --config 2a
```

```bash
dotnet run --project scripts/Runner -- emulator-check
```

```powershell
./scripts/faults.ps1 postgres-down
```

Against a real namespace instead of the emulator: deploy `infra/main.bicep`, then set `ConnectionStrings__servicebus`
to the namespace's connection string, in the environment of both the AppHost and the Runner. With it set the emulator
doesn't start, and a run's folder gets `-cloud` after any `-cfg<label>`. The queue's properties then come from the
template, so `Experiment:MaxDeliveryCount` has no effect on the queue, and `emulator-check` reads them from the
namespace.

| Scenario               | Fault                                                                  |
|------------------------|------------------------------------------------------------------------|
| `smoke`                | none; 10 messages, expect exactly 1 attempt per message at every layer |
| `A-postgres-down-2min` | toxiproxy proxy disabled for 2 minutes                                 |
| `A-postgres-down-*`    | the same for 30 s, 5, 10 and 20 minutes, for the outage-length sweep   |
| `B-postgres-slow-15s`  | 15 s latency on the Postgres connection                                |
| `C-api-errors-20pct`   | OrdersApi returns 500 for 20% of requests                              |
| `D-brownout`           | capacity-limited, slow OrdersApi; **not defaults**, see Switches       |

Each scenario resets the counters, truncates the table, purges the queue, clears faults, starts the producer, applies
and removes the fault on schedule, stops the producer and waits for the queue to drain: every sent message completed or
dead-lettered and none in progress, for 5 samples in a row.

`emulator-check` checks three things the experiment relies on and prints PASS or FAIL for each: the queue properties
read back, delivery counts 1 to 10 and then the dead-letter queue, and redelivery after a lost lock. It takes about 5
minutes.

## Results

Each run writes to `results/{scenario}-{timestamp}/`, which git keeps. With `--dev` it writes to
`results/dev/{scenario}-{timestamp}/` instead, which git ignores: use it for every run the post doesn't report. The
`scenario-*` runs started from the dashboard pass `--dev`, so baseline runs are started from a terminal.
`--config <label>` adds `-cfg<label>` after the scenario name and writes the label to `config.json` (`defaults`
without it). Each run folder has:

- `timeseries.csv`: one row per second with every counter from every service and the database, plus
  `toxiproxy.accepted_clients`, the TCP connections toxiproxy accepted so far, and `runner.dlq_peeked`, the messages in
  the dead-letter queue, which the Runner counts by peeking about once a second. `worker.queue_active` and
  `worker.queue_dead_lettered` are the queue's runtime properties, for a real namespace. The emulator reports 0 for
  every queue count, so on the emulator the Worker doesn't poll them and both are -1 (not available)
- `summary.json`: totals and per-message ratios at each layer, plus duplicates. `attemptsPerMessage` divides by every
  message sent, so it hides the messages the fault hit; `hitMessages` covers only those (any layer's count differs from
  1 delivery, 1 HTTP attempt, 1 request, 1 connection open, 1 command, 0 EF retries, 1 row). `deadLettered` has
  `fromPeekingDlq`, a peek of the dead-letter queue at the end of the run, and `fromQueueRuntimeProperties`, null on the
  emulator
- Two layers in `attemptsPerMessage` count physical connections rather than opens, which the pool mostly serves:
  `npgsqlConnectionsCreated` (new connections that opened, from Npgsql's `create_time` metric) and
  `toxiproxyConnections` (`Accepted client` lines in toxiproxy's container log, read with `docker logs`). toxiproxy sees
  failed opens too, so it should equal `npgsqlConnectionsCreated` plus `db_connection_failed`, except that an open
  rejected by Postgres (a wrong password, say) makes 2 connections, because Npgsql tries again without GSS encryption.
  It sees nothing in fault A: the proxy is disabled, and Docker's port forwarder accepts and closes the connections
  before they reach it
- `recovery` in `summary.json`: seconds from the fault's removal until the backlog (`producer.sent` minus
  `worker.completed` minus `runner.dlq_peeked`) is back to its largest value during warmup and stays there to the end of
  the run, from the 1 s samples. Null if it never gets back, or if the scenario has no fault
- `messages.csv`: one row per message with its count at each layer, the messages the fault hit first
- `config.json`: the scenario, and each service's `/admin/config` (switches, images, retry options) at the start of the
  run

The services expose the same numbers live on `GET /admin/stats` (Producer :5101, Worker :5102, OrdersApi :5103), and
OrdersApi on `GET /admin/groundtruth`. The Worker and OrdersApi also count per message, on `GET /admin/stats/messages`,
and OrdersApi gives the rows per message on `GET /admin/groundtruth/messages`. Metrics go to the Aspire dashboard under
the `Experiment` meter.

## Switches

Set in `src/AppHost/appsettings.json` under `Experiment`, or as environment variables `Experiment__<Name>` on the
AppHost. The Worker switches can also be changed at runtime with `PUT /admin/experiment` on the Worker.

| Switch               | Default | Effect                                                                  |
|----------------------|---------|-------------------------------------------------------------------------|
| `HttpRetries`        | true    | false: the standard resilience handler's retry strategy handles nothing |
| `DbRetries`          | true    | false: `DisableRetry` on the Aspire EF Core integration                 |
| `PassCancellation`   | false   | true: OrdersApi passes `HttpContext.RequestAborted` to EF Core          |
| `AlwaysThrow`        | false   | true: the Worker handler throws before calling the API                  |
| `HoldMessageMs`      | 0       | The Worker handler waits this long before calling the API               |
| `DisableLockRenewal` | false   | true: `MaxAutoLockRenewalDuration = 0`                                  |
| `TunedBreaker`       | false   | true: Worker breaker at 50% failures, minimum 2 in 30 s, break 30 s     |
| `RetryBudget`        | false   | true: HTTP and EF retries at most 10% of first attempts, burst 10       |
| `DelayedRedelivery`  | false   | true: a failed message is scheduled again, 5 s doubling to 5 min        |
| `MaxDeliveryCount`   | 10      | The queue's `MaxDeliveryCount`; 1 turns Service Bus redelivery off      |
| `MaxConcurrentCalls` | 1       | The Worker processor's `MaxConcurrentCalls`, the SDK default            |

A scenario can set Worker switches for its own run with `workerSwitches`, the body of `PUT /admin/experiment`. Before
every run the Runner puts the Worker on those switches, or back on its startup ones, so a scenario never inherits
another's. Only `D-brownout` uses it: it raises `MaxConcurrentCalls`, because with 1 request in flight the API never
queues. **So fault D does not run on defaults**, unlike every other scenario.

## Configurations

Each configuration is a set of switches on the AppHost, and the run's `--config` label puts it in the folder name. Set
the switches in the AppHost's environment before starting it, because `TunedBreaker`, `RetryBudget` and
`DelayedRedelivery` are read at startup. `appsettings.json` keeps the defaults.

| Label | Switches                                                     | What it tries                         |
|-------|--------------------------------------------------------------|---------------------------------------|
| none  | none                                                         | The defaults                          |
| `2a`  | `HttpRetries` false, `DbRetries` false                       | Retry at Service Bus only             |
| `2b`  | `MaxDeliveryCount` 1                                         | Retry at HTTP and EF only             |
| `3`   | `PassCancellation` true                                      | Cancellation all the way down         |
| `4`   | `TunedBreaker` true                                          | A breaker that trips at this traffic  |
| `5`   | `RetryBudget` true                                           | A retry budget                        |
| `6`   | `DelayedRedelivery` true                                     | Delayed redelivery                    |
| `46`  | `TunedBreaker` true, `DelayedRedelivery` true                | The breaker, without its dead letters |
| `346` | `PassCancellation`, `TunedBreaker`, `DelayedRedelivery` true | The fix charted against the defaults  |

For example, configuration 6 against the 5 minute outage, with the AppHost in one terminal and the Runner in another:

```powershell
$env:Experiment__DelayedRedelivery = "true"; dotnet run --project src/AppHost
```

```bash
dotnet run --project scripts/Runner -- A-postgres-down-5min --config 6
```

## The runs in `results/`

| Runs                                              | Where          | Configurations                                |
|---------------------------------------------------|----------------|-----------------------------------------------|
| `A-postgres-down-2min`, `B-postgres-slow-15s`     | Emulator       | Defaults, 2a, 2b, 4, 5, 6, 46, 346 (B also 3) |
| `C-api-errors-20pct`                              | Emulator       | Defaults, 2a, 2b, 4, 5, 6, 46                 |
| `A-postgres-down-{30s,5min,10min,20min}`          | Emulator       | Defaults, 6                                   |
| `A-postgres-down-2min-cloud-*`                    | Real namespace | Defaults, two runs                            |
| `D-brownout-cloud-*`, `D-brownout-cfg346-cloud-*` | Real namespace | Defaults and 346, two runs each               |

Most comparisons are one run of each, so a small difference isn't evidence of much. Fault A on both shows the emulator
is sound for counts but not for time: the attempts match to within a retry or two, while recovery is about 4.6 times
longer on the real namespace. The emulator reports no queue counts either, so dead letters come from peeking the
dead-letter queue. Reproducing the `-cloud` runs needs your own namespace, which costs money while it exists.

Not explained, and left as they are:

- `B-postgres-slow-15s-20261026-231055`: toxiproxy accepted 24 connections, against 4 created plus 19 failed. A re-run
  gave 24 = 5 + 19, so it didn't reproduce
- Configuration 2 against fault C sent fewer than 1,800 messages (1,796 and 1,768). Rows plus dead letters equal the sent
  count, so every sent message is accounted for
- `C-api-errors-20pct-cfg2b` has no recovery time: its backlog never stayed back at the warmup value of 0, so
  `recoverySeconds` is null. The run drained

## Charts

The charts are Astro components for the post's site, and Astro isn't a dependency of this repo, so copy
`charts/components/` (with its `data/` folder) into an Astro project to render them. They read their colours and font
from the site's `--bg`, `--fg`, `--muted`, `--accent`, `--line` and `--font-display` CSS variables.

Regenerate the data from the result folders with Node, which needs no packages:

```bash
node charts/export-data.mjs
```

The script names the runs it reads at its top, so to chart your own runs, change those names.

| Component             | Data                          | Runs                                           |
|-----------------------|-------------------------------|------------------------------------------------|
| `ChainDiagram`        | hard-coded                    | None; the values are from `docs/defaults.md`   |
| `AttemptsPerLayer`    | `attempts-per-layer.json`     | Faults A and B, defaults against 346, emulator |
| `DeadLettersVsOutage` | `dead-letters-vs-outage.json` | Fault A sweep, defaults against 6, emulator    |
| `BrownoutMessages`    | `brownout-messages.json`      | Fault D, defaults against 346, real namespace  |

`images/04-trace-waterfall.jpg` is a screenshot of the Aspire dashboard during a real namespace run of fault A, so it
comes from running that scenario, not from a script.

## Not tried

- Idempotency: a unique constraint on `message_id` or an idempotency key
- Timeouts that fit inside each other (HTTP attempt timeout vs EF retries vs lock duration)
- A sweep of `MaxConcurrentCalls` for fault D
- The same experiment on RabbitMQ
