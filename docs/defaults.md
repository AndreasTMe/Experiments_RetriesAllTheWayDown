# Defaults in play

Every retry, timeout and limit that affects this experiment, as it is in the baseline. Nothing here was tuned.

How each value was checked:

- **probe**: read at runtime from an object built with the installed package, e.g.
  `new HttpStandardResilienceOptions()`, `new ServiceBusProcessorOptions()`, `new NpgsqlConnectionStringBuilder()`.
- **runtime**: reported by the running services on `GET /admin/config`, or read back from the emulator.
- **source**: read in the package source at the release tag, or in the package's XML docs.
- **UNVERIFIED**: taken from documentation or memory and not checked against the installed package.

## Versions

| Component                                                  | Version  |
|------------------------------------------------------------|----------|
| .NET SDK                                                   | 10.0.112 |
| Aspire (AppHost SDK, hosting and client integrations, CLI) | 13.6.4   |
| Azure.Messaging.ServiceBus                                 | 7.20.2   |
| Microsoft.Extensions.Http.Resilience                       | 10.11.0  |
| Polly.Core                                                 | 8.4.2    |
| Npgsql                                                     | 10.0.3   |
| Npgsql.EntityFrameworkCore.PostgreSQL                      | 10.0.3   |
| Microsoft.EntityFrameworkCore                              | 10.0.12  |
| OpenTelemetry (service defaults template)                  | 1.20.1   |

## Container images

| Image                                                 | Tag                    | Note                                                                               |
|-------------------------------------------------------|------------------------|------------------------------------------------------------------------------------|
| docker.io/library/postgres                            | 18.3                   | `-c shared_preload_libraries=pg_stat_statements`                                   |
| mcr.microsoft.com/azure-messaging/servicebus-emulator | 2.0.0                  |                                                                                    |
| mcr.microsoft.com/mssql/server                        | 2022-CU27-ubuntu-22.04 | Aspire adds this for the emulator with tag `2022-latest`; re-pinned in the AppHost |
| ghcr.io/shopify/toxiproxy                             | 2.13.0                 | proxy `postgres` listens on 15432, upstream `external-postgres:5432`               |
| docker.io/sosedoff/pgweb                              | 0.16.2                 | `WithPgWeb`, connects to Postgres directly (not through toxiproxy)                 |

## Service Bus

| Setting                                               | Value                                                                                                                                                       | Package                                          | Version | Source              |
|-------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------------|---------|---------------------|
| ServiceBusProcessorOptions.MaxConcurrentCalls         | 1 (switch `MaxConcurrentCalls`; fault D raises it, the only setting a baseline run changes)                                                                 | Azure.Messaging.ServiceBus                       | 7.20.2  | probe, runtime      |
| ServiceBusProcessorOptions.PrefetchCount              | 0                                                                                                                                                           | Azure.Messaging.ServiceBus                       | 7.20.2  | probe, runtime      |
| ServiceBusProcessorOptions.AutoCompleteMessages       | true                                                                                                                                                        | Azure.Messaging.ServiceBus                       | 7.20.2  | probe, runtime      |
| ServiceBusProcessorOptions.MaxAutoLockRenewalDuration | 5 min (0 with `DisableLockRenewal`)                                                                                                                         | Azure.Messaging.ServiceBus                       | 7.20.2  | probe, runtime      |
| ServiceBusProcessorOptions.ReceiveMode                | PeekLock                                                                                                                                                    | Azure.Messaging.ServiceBus                       | 7.20.2  | probe, runtime      |
| ServiceBusRetryOptions.Mode                           | Exponential                                                                                                                                                 | Azure.Messaging.ServiceBus                       | 7.20.2  | probe, runtime      |
| ServiceBusRetryOptions.MaxRetries                     | 3                                                                                                                                                           | Azure.Messaging.ServiceBus                       | 7.20.2  | probe, runtime      |
| ServiceBusRetryOptions.Delay                          | 0.8 s                                                                                                                                                       | Azure.Messaging.ServiceBus                       | 7.20.2  | probe, runtime      |
| ServiceBusRetryOptions.MaxDelay                       | 60 s                                                                                                                                                        | Azure.Messaging.ServiceBus                       | 7.20.2  | probe, runtime      |
| ServiceBusRetryOptions.TryTimeout                     | 60 s                                                                                                                                                        | Azure.Messaging.ServiceBus                       | 7.20.2  | probe, runtime      |
| Handler cancellation token                            | cancelled only by `StopProcessingAsync`, not when the lock expires                                                                                          | Azure.Messaging.ServiceBus                       | 7.20.2  | source              |
| Client tracing                                        | off unless `AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE=true` (set by the AppHost on producer and worker)                                                     | Aspire.Azure.Messaging.ServiceBus                | 13.6.4  | source              |
| Queue `orders` MaxDeliveryCount                       | 10 (switch `MaxDeliveryCount`)                                                                                                                              | set in AppHost (Aspire.Hosting.Azure.ServiceBus) | 13.6.4  | runtime (read back) |
| Queue `orders` LockDuration                           | 1 min                                                                                                                                                       | set in AppHost                                   | 13.6.4  | runtime (read back) |
| Queue `orders` DeadLetteringOnMessageExpiration       | false                                                                                                                                                       | set in AppHost                                   | 13.6.4  | runtime (read back) |
| Queue `orders` RequiresSession                        | false                                                                                                                                                       | set in AppHost                                   | 13.6.4  | runtime (read back) |
| Queue `orders` RequiresDuplicateDetection             | false                                                                                                                                                       | set in AppHost                                   | 13.6.4  | runtime (read back) |
| Queue `orders` DefaultMessageTimeToLive               | 1 h                                                                                                                                                         | emulator default                                 | 2.0.0   | runtime (read back) |
| Queue `orders` DuplicateDetectionHistoryTimeWindow    | 10 min (unused, duplicate detection off)                                                                                                                    | emulator default                                 | 2.0.0   | runtime (read back) |
| Queue `orders` EnableBatchedOperations                | true                                                                                                                                                        | emulator default                                 | 2.0.0   | runtime (read back) |
| Queue `orders` AutoDeleteOnIdle                       | TimeSpan.MaxValue (never)                                                                                                                                   | emulator default                                 | 2.0.0   | runtime (read back) |
| Redelivery after abandon                              | immediate: 6 to 37 ms between deliveries 1 to 10 in `emulator-check`, then dead-lettered with `MaxDeliveryCountExceeded`                                    | emulator                                         | 2.0.0   | runtime             |
| Redelivery after lock loss                            | lock expires after LockDuration; complete fails with `MessageLockLost`; redelivered with DeliveryCount 2 (70.3 s after the first delivery with a 70 s hold) | emulator                                         | 2.0.0   | runtime             |
| Emulator vs real Service Bus                          | the emulator's behaviour was not compared with a real namespace                                                                                             |                                                  |         | UNVERIFIED          |

## HttpClient (Worker → OrdersApi), standard resilience handler from the service defaults

| Setting                                         | Value                                                                                                            | Package                              | Version | Source  |
|-------------------------------------------------|------------------------------------------------------------------------------------------------------------------|--------------------------------------|---------|---------|
| Pipeline order                                  | RateLimiter → TotalRequestTimeout → Retry → CircuitBreaker → AttemptTimeout                                      | Microsoft.Extensions.Http.Resilience | 10.11.0 | source  |
| RateLimiter PermitLimit / QueueLimit            | 1000 / 0                                                                                                         | Microsoft.Extensions.Http.Resilience | 10.11.0 | probe   |
| TotalRequestTimeout                             | 30 s                                                                                                             | Microsoft.Extensions.Http.Resilience | 10.11.0 | probe   |
| Retry MaxRetryAttempts                          | 3 (4 attempts in total)                                                                                          | Microsoft.Extensions.Http.Resilience | 10.11.0 | probe   |
| Retry BackoffType / UseJitter                   | Exponential / true                                                                                               | Microsoft.Extensions.Http.Resilience | 10.11.0 | probe   |
| Retry Delay / MaxDelay                          | 2 s / none                                                                                                       | Microsoft.Extensions.Http.Resilience | 10.11.0 | probe   |
| Retry ShouldRetryAfterHeader                    | true                                                                                                             | Microsoft.Extensions.Http.Resilience | 10.11.0 | probe   |
| Retry on POST                                   | yes (`DisableForUnsafeHttpMethods` is opt-in and not called)                                                     | Microsoft.Extensions.Http.Resilience | 10.11.0 | source  |
| Transient outcomes                              | 408, 429, 5xx, `HttpRequestException`, `TimeoutRejectedException`; not 400/404, not `OperationCanceledException` | Microsoft.Extensions.Http.Resilience | 10.11.0 | probe   |
| CircuitBreaker FailureRatio / MinimumThroughput | 0.1 / 100                                                                                                        | Microsoft.Extensions.Http.Resilience | 10.11.0 | probe   |
| CircuitBreaker SamplingDuration / BreakDuration | 30 s / 5 s                                                                                                       | Microsoft.Extensions.Http.Resilience | 10.11.0 | probe   |
| AttemptTimeout                                  | 10 s                                                                                                             | Microsoft.Extensions.Http.Resilience | 10.11.0 | probe   |
| HttpClient.Timeout                              | 100 s (outside the pipeline, so the 30 s total timeout hits first)                                               | System.Net.Http                      | .NET 10 | probe   |
| `HttpRetries=false`                             | retry `ShouldHandle` returns false; the rest of the pipeline stays                                               | this repo                            |         | runtime |

## EF Core and Npgsql (OrdersApi → toxiproxy → Postgres)

| Setting                            | Value                                                                                                                                    | Package                                      | Version | Source                                     |
|------------------------------------|------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------|---------|--------------------------------------------|
| Aspire enables EF retry by default | **yes**: `NpgsqlEntityFrameworkCorePostgreSQLSettings.DisableRetry` defaults to false and the integration calls `EnableRetryOnFailure()` | Aspire.Npgsql.EntityFrameworkCore.PostgreSQL | 13.6.4  | source, runtime (`retriesOnFailure: true`) |
| Execution strategy                 | `NpgsqlRetryingExecutionStrategy` (here subclassed as `RetryCountingExecutionStrategy` only to count retries)                            | Npgsql.EntityFrameworkCore.PostgreSQL        | 10.0.3  | probe, runtime                             |
| MaxRetryCount                      | 6 (7 attempts in total)                                                                                                                  | Npgsql.EntityFrameworkCore.PostgreSQL        | 10.0.3  | probe                                      |
| MaxRetryDelay                      | 30 s                                                                                                                                     | Npgsql.EntityFrameworkCore.PostgreSQL        | 10.0.3  | probe                                      |
| Retry delays (sampled, jittered)   | ≈ 0, 1, 3.2, 7.1, 15.5, 30 s; the first retry is immediate                                                                               | Microsoft.EntityFrameworkCore                | 10.0.12 | probe                                      |
| EF CommandTimeout                  | not set, so the Npgsql value applies                                                                                                     | Microsoft.EntityFrameworkCore                | 10.0.12 | probe                                      |
| Npgsql Timeout (connect)           | 15 s                                                                                                                                     | Npgsql                                       | 10.0.3  | probe, runtime                             |
| Npgsql CommandTimeout              | 30 s                                                                                                                                     | Npgsql                                       | 10.0.3  | probe, runtime                             |
| Npgsql Pooling / MaxPoolSize       | true / 100                                                                                                                               | Npgsql                                       | 10.0.3  | probe, runtime                             |
| Npgsql KeepAlive                   | 0 (off)                                                                                                                                  | Npgsql                                       | 10.0.3  | probe                                      |
| Npgsql Gss Encryption Mode         | Prefer: an open that fails tries again without GSS encryption, on a second TCP connection; a successful open makes one                   | Npgsql                                       | 10.0.3  | toxiproxy log                              |
| Npgsql CancellationTimeout         | 2000 ms                                                                                                                                  | Npgsql                                       | 10.0.3  | probe                                      |
| `RequestAborted` passed to EF      | no (`PassCancellation=false`)                                                                                                            | this repo                                    |         | runtime                                    |
| Unique constraint on `message_id`  | none                                                                                                                                     | this repo                                    |         | runtime                                    |

## Aspire hosting

| Setting                          | Value                                                                                                                                                                                                            | Package                                    | Version | Source                                          |
|----------------------------------|------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|--------------------------------------------|---------|-------------------------------------------------|
| Container-to-container names     | containers resolve each other by resource name (toxiproxy reaches `external-postgres:5432`)                                                                                                                      | Aspire.Hosting                             | 13.6.4  | runtime                                         |
| `AddPostgres` without a password | creates a `postgres-password` parameter that is **not added** to the app model; a `ConnectionString` resource referencing it stays "Waiting for dependencies" forever. The AppHost adds the parameter explicitly | Aspire.Hosting / Aspire.Hosting.PostgreSQL | 13.6.4  | source (XML docs: "Creates" vs "Adds"), runtime |
| Emulator connection string       | `Endpoint=sb://host:port;SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;`                                                                               | Aspire.Hosting.Azure.ServiceBus            | 13.6.4  | source, runtime                                 |
| Emulator management port         | 5300 (the `emulatorhealth` endpoint); `ServiceBusAdministrationClient` works against it                                                                                                                          | Aspire.Hosting.Azure.ServiceBus            | 13.6.4  | runtime                                         |
