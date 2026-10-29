# Retries all the way down

When a dependency fails, every layer between the user and that dependency retries on its own, and the retries multiply.
This repo measures that multiplication in a small, realistic .NET Aspire system: a producer puts orders on a Service Bus
queue, a worker takes each message and POSTs it to an API, and the API inserts a row into Postgres through EF Core. Each
layer runs with the defaults its library ships with: the Service Bus redelivery, the HttpClient standard resilience
handler, and the EF Core retrying execution strategy that Aspire turns on.

This is the baseline: it has no fixes. Faults are injected between the API and Postgres with toxiproxy, or inside the
API with a fault middleware. Every layer counts what it does (deliveries, HTTP attempts, requests, connection opens,
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
| `DelayedRedelivery`  | false   | true: a failed message is scheduled again, 5 s doubling to 5 min        |
| `MaxDeliveryCount`   | 10      | The queue's `MaxDeliveryCount`; 1 turns Service Bus redelivery off      |
| `MaxConcurrentCalls` | 1       | The Worker processor's `MaxConcurrentCalls`, the SDK default            |

A scenario can set Worker switches for its own run with `workerSwitches`, the body of `PUT /admin/experiment`. Before
every run the Runner puts the Worker on those switches, or back on its startup ones, so a scenario never inherits
another's. Only `D-brownout` uses it: it raises `MaxConcurrentCalls`, because with 1 request in flight the API never
queues. **So fault D does not run on defaults**, unlike every other scenario.

## TODO

Not implemented on purpose; this is the baseline to compare them against.

- Circuit breaker tuning (the standard handler's breaker needs 100 requests in 30 s before it can open)
- Retry budgets, or retrying at one layer only
- Delayed redelivery (scheduled messages or backoff instead of an immediate abandon)
- Idempotency: a unique constraint on `message_id` or an idempotency key
- Timeouts that fit inside each other (HTTP attempt timeout vs EF retries vs lock duration)
- Passing cancellation through by default
