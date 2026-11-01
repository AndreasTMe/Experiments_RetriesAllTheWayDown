namespace Runner.Commands;

internal sealed class ScenarioCommand
{
    private readonly List<Dictionary<string, string>> _samples = [];
    private readonly ExperimentClients                _clients;
    private readonly string                           _resultsRoot;
    // The --config label, null for a defaults run.
    private readonly string?                          _configuration;

    private string  _phase = "setup";
    private double? _faultRemovedAtSeconds;
    // Written by CountDeadLettersEverySecondAsync; -1 until the first count.
    private long    _deadLettered = -1;

    public ScenarioCommand(ExperimentClients clients, string resultsRoot, string? configuration)
    {
        _clients       = clients;
        _resultsRoot   = resultsRoot;
        _configuration = configuration;
    }

    public async Task<int> RunAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var suffix = (_configuration is null ? "" : $"-cfg{_configuration}")
                     + (_clients.Settings.UsesEmulator ? "" : "-cloud");
        var resultsDir = Path.Combine(_resultsRoot, $"{scenario.Name}{suffix}-{startedAt:yyyyMMdd-HHmmss}");
        Directory.CreateDirectory(resultsDir);
        Log($"Scenario {scenario.Name}: results in {resultsDir}");

        await ResetAsync(scenario, cancellationToken);

        // Write the config before the run so it survives a crash.
        var config = await CollectConfigAsync(scenario, cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(resultsDir, "config.json"),
            config.ToJsonString(JsonDefaults.Options),
            cancellationToken);

        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var       clock    = Stopwatch.StartNew();
        var       sampler  = SampleEverySecondAsync(clock, sampling.Token);
        var       counter  = CountDeadLettersEverySecondAsync(sampling.Token);

        // 1. Producer
        _phase = "warmup";
        await ExperimentClients.PostAsync(
            _clients.Producer,
            BuildStartPath(scenario.Rate, scenario.MessageCount),
            cancellationToken);
        Log($"Producer started at {scenario.Rate}/s{(scenario.MessageCount is { } n ? $", {n} messages" : "")}");

        // 2. Fault, rate changes and producer stop, on schedule
        var events = new List<(int At, string Label, Func<CancellationToken, Task> Action)>();
        if (scenario.Fault.Kind != "none")
        {
            events.Add(
                (scenario.FaultAppliedAt, $"apply fault {scenario.Fault.Kind}", async ct =>
                {
                    await scenario.Fault.ApplyAsync(_clients, ct);
                    _phase = "fault";
                }));
            events.Add(
                (scenario.FaultRemovedAt, $"remove fault {scenario.Fault.Kind}", async ct =>
                {
                    await scenario.Fault.RemoveAsync(_clients, ct);
                    _phase                 = "recovery";
                    _faultRemovedAtSeconds = clock.Elapsed.TotalSeconds;
                }));
        }

        foreach (var change in scenario.RateChanges)
        {
            events.Add(
                (change.AtSeconds, $"rate -> {change.Rate}/s",
                    ct => ExperimentClients.PostAsync(_clients.Producer, BuildStartPath(change.Rate, null), ct)));
        }

        if (scenario.MessageCount is null)
        {
            events.Add(
                (scenario.ProducerStopsAt, "stop producer",
                    ct => ExperimentClients.PostAsync(_clients.Producer, "/stop", ct)));
        }

        foreach (var (at, label, action) in events.OrderBy(e => e.At))
        {
            var wait = TimeSpan.FromSeconds(at) - clock.Elapsed;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken);
            }

            Log($"t={clock.Elapsed.TotalSeconds:F0}s {label}");
            await action(cancellationToken);
        }

        if (scenario.MessageCount is not null)
        {
            await WaitForProducerAsync(scenario.MessageCount.Value, cancellationToken);
            await ExperimentClients.PostAsync(_clients.Producer, "/stop", cancellationToken);
        }

        // 3. Drain
        _phase = "drain";
        Log(
            $"t={clock.Elapsed.TotalSeconds:F0}s producer stopped, waiting up to {scenario.DrainTimeoutSeconds}s for the queue to drain");
        var drained = await WaitForDrainAsync(TimeSpan.FromSeconds(scenario.DrainTimeoutSeconds), cancellationToken);
        Log(
            drained
                ? $"t={clock.Elapsed.TotalSeconds:F0}s drained"
                : $"t={clock.Elapsed.TotalSeconds:F0}s drain timeout reached");

        // A few more samples so the tail is visible.
        _phase = "done";
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
        await sampling.CancelAsync();
        await sampler;
        await counter;

        var accepted = await ReadToxiproxyAcceptedAsync(startedAt, cancellationToken);
        AddToxiproxyColumn(accepted);
        await WriteTimeseriesAsync(Path.Combine(resultsDir, "timeseries.csv"), cancellationToken);
        var perMessage = await PerMessageCounts.CollectAsync(_clients, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(resultsDir, "messages.csv"), perMessage.ToCsv(), cancellationToken);
        var summary = await BuildSummaryAsync(scenario, startedAt, drained, perMessage, accepted, cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(resultsDir, "summary.json"),
            summary.ToJsonString(JsonDefaults.Options),
            cancellationToken);

        Log(
            $"Wrote timeseries.csv ({_samples.Count} samples), messages.csv, summary.json and config.json to {resultsDir}");
        Console.WriteLine(summary["attemptsPerMessage"]!.ToJsonString(JsonDefaults.Options));
        Console.WriteLine(summary["hitMessages"]!.ToJsonString(JsonDefaults.Options));
        Console.WriteLine(summary["recovery"]!.ToJsonString(JsonDefaults.Options));

        if (scenario.WorkerSwitches is not null)
        {
            Log("Putting the Worker back on its startup switches");
            await _clients.SetWorkerSwitchesAsync(null, cancellationToken);
        }

        return 0;
    }

    // Clean slate: no faults, empty queue and DLQ, empty table, zeroed counters and pg_stat_statements, and the
    // Worker on the scenario's switches, or on its startup ones so a scenario never inherits another's.
    private async Task ResetAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        Log("Resetting");
        await ExperimentClients.PostAsync(_clients.Producer, "/stop", cancellationToken);
        await _clients.Toxiproxy.ResetAsync(cancellationToken);
        await _clients.ClearApiFaultsAsync(cancellationToken);

        var (active, deadLettered) = await _clients.PurgeQueueAsync(cancellationToken);
        Log($"Purged {active} active and {deadLettered} dead-lettered messages");

        await WaitUntilAsync(
            async ct => await GetGaugeAsync("worker", "in_flight", ct) == 0,
            TimeSpan.FromSeconds(120),
            cancellationToken);

        Log(
            scenario.WorkerSwitches is null
                ? "Worker on its startup switches"
                : $"Worker switches for this run: {scenario.WorkerSwitches.ToJsonString()}");
        await _clients.SetWorkerSwitchesAsync(scenario.WorkerSwitches, cancellationToken);

        await ExperimentClients.PostAsync(_clients.OrdersApi, "/admin/groundtruth/reset", cancellationToken);
        foreach (var (_, client) in _clients.All)
        {
            await ExperimentClients.PostAsync(client, "/admin/reset", cancellationToken);
        }
    }

    private async Task<JsonObject> CollectConfigAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        var config = new JsonObject
        {
            ["scenario"]      = JsonSerializer.SerializeToNode(scenario, JsonDefaults.Options),
            ["configuration"] = _configuration ?? "defaults",
            ["runner"] = JsonSerializer.SerializeToNode(
                _clients.Settings with
                {
                    ServiceBus = Redact(_clients.Settings.ServiceBus),
                    ServiceBusAdmin = Redact(_clients.Settings.ServiceBusAdmin)
                },
                JsonDefaults.Options),
        };

        foreach (var (name, client) in _clients.All)
        {
            config[name] = await ExperimentClients.GetJsonAsync(client, "/admin/config", cancellationToken);
        }

        config["toxiproxy"] = new JsonObject
        {
            ["version"] = await _clients.Toxiproxy.GetVersionAsync(cancellationToken),
            ["proxy"]   = await _clients.Toxiproxy.GetProxyAsync(_clients.Settings.Proxy, cancellationToken),
        };

        return config;
    }

    private async Task SampleEverySecondAsync(Stopwatch clock, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        do
        {
            try
            {
                _samples.Add(await TakeSampleAsync(clock, cancellationToken));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                Log($"Sample failed: {ex.Message}");
            }
        } while (await WaitForTickAsync(timer, cancellationToken));

        static async Task<bool> WaitForTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
        {
            try
            {
                return await timer.WaitForNextTickAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return false;
            }
        }
    }

    // Its own loop rather than part of the sample: an emulator peek now and then stalls for about 12 s, and the samples
    // must not wait for it. Only the dead-letter queue is peeked; peeking the main queue every second slowed the worker
    // down about 3 times.
    private async Task CountDeadLettersEverySecondAsync(CancellationToken cancellationToken)
    {
        await using var receiver = _clients.ServiceBus.CreateReceiver(
            _clients.Settings.Queue,
            new ServiceBusReceiverOptions
            {
                SubQueue = SubQueue.DeadLetter
            });
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            do
            {
                try
                {
                    Interlocked.Exchange(ref _deadLettered, await CountByPeekingAsync(receiver, cancellationToken));
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    Log($"Counting dead letters failed: {ex.Message}");
                }
            } while (await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task<Dictionary<string, string>> TakeSampleAsync(Stopwatch clock, CancellationToken cancellationToken)
    {
        var row = new Dictionary<string, string>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["elapsed_s"] = clock.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture),
            ["phase"]     = _phase,
        };

        var deadLettered = Interlocked.Read(ref _deadLettered);
        if (deadLettered >= 0)
        {
            row["runner.dlq_peeked"] = deadLettered.ToString(CultureInfo.InvariantCulture);
        }

        var stats = await Task.WhenAll(
            _clients.All.Select(async s =>
                (s.Name, Stats: await TryGetJsonAsync(s.Client, "/admin/stats", cancellationToken))));
        foreach (var (name, json) in stats)
        {
            if (json is null)
            {
                row[$"{name}.up"] = "0";
                continue;
            }

            row[$"{name}.up"] = "1";
            foreach (var section in new[]
                     {
                         "counters", "gauges"
                     })
            {
                foreach (var (key, value) in json[section]!.AsObject())
                {
                    row[$"{name}.{key}"] = value!.ToString();
                }
            }
        }

        var groundTruth = await TryGetJsonAsync(_clients.OrdersApi, "/admin/groundtruth", cancellationToken);
        if (groundTruth is not null)
        {
            foreach (var (key, value) in groundTruth.AsObject())
            {
                if (key != "timestamp")
                {
                    row[$"db.{key}"] = value!.ToString();
                }
            }
        }

        return row;
    }

    // Null when the log can't be read (no docker, no container): the run's other results are still worth keeping.
    private async Task<List<DateTimeOffset>?> ReadToxiproxyAcceptedAsync(
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _clients.ToxiproxyLog.ReadAcceptedClientsAsync(
                _clients.Settings.Proxy,
                since,
                cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log($"Could not count toxiproxy connections: {ex.Message}");
            return null;
        }
    }

    // Cumulative connections accepted, per sample. toxiproxy logs whole seconds, so a connection counts from the start
    // of the second it was logged in, up to a second before the sample that saw its effect.
    private void AddToxiproxyColumn(List<DateTimeOffset>? accepted)
    {
        if (accepted is null)
        {
            return;
        }

        foreach (var sample in _samples)
        {
            var at = DateTimeOffset.Parse(sample["timestamp"], CultureInfo.InvariantCulture);
            sample["toxiproxy.accepted_clients"] = accepted.Count(t => t.ToUnixTimeSeconds() <= at.ToUnixTimeSeconds())
                .ToString(CultureInfo.InvariantCulture);
        }
    }

    private async Task WriteTimeseriesAsync(string path, CancellationToken cancellationToken)
    {
        // Union of every column seen, so counters that appear late still get a column.
        var columns = new List<string>
        {
            "timestamp",
            "elapsed_s",
            "phase"
        };
        columns.AddRange(
            _samples.SelectMany(s => s.Keys).Distinct().Where(k => !columns.Contains(k)).Order(StringComparer.Ordinal));

        var csv = new StringBuilder();
        csv.AppendLine(string.Join(',', columns));
        foreach (var sample in _samples)
        {
            csv.AppendLine(string.Join(',', columns.Select(c => sample.TryGetValue(c, out var v) ? v : "")));
        }

        await File.WriteAllTextAsync(path, csv.ToString(), cancellationToken);
    }

    private async Task WaitForProducerAsync(int count, CancellationToken cancellationToken)
    {
        await WaitUntilAsync(
            async ct => await GetCounterAsync("producer", "sent", ct)
                        + await GetCounterAsync("producer", "send_failed", ct)
                        >= count,
            TimeSpan.FromMinutes(5),
            cancellationToken);
    }

    // Drained = every sent message completed or dead-lettered, and the worker is not processing any, for 5 samples in
    // a row. It doesn't use the queue's active count, which the emulator reports as 0. The dead-letter count can be up
    // to about 12 s old (see CountDeadLettersEverySecondAsync), which only makes the drain later.
    private async Task<bool> WaitForDrainAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var quietSamples = 0;
        return await WaitUntilAsync(
            async ct =>
            {
                var sent         = await GetCounterAsync("producer", "sent", ct);
                var completed    = await GetGaugeAsync("worker", "completed", ct);
                var inFlight     = await GetGaugeAsync("worker", "in_flight", ct);
                var deadLettered = Math.Max(0, Interlocked.Read(ref _deadLettered));
                var empty        = inFlight == 0 && completed + deadLettered >= sent;

                quietSamples = empty ? quietSamples + 1 : 0;
                return quietSamples >= 5;
            },
            timeout,
            cancellationToken);
    }

    private Task<long> GetCounterAsync(string service, string name, CancellationToken cancellationToken) =>
        ReadStatAsync(service, "counters", name, cancellationToken);

    private Task<long> GetGaugeAsync(string service, string name, CancellationToken cancellationToken) =>
        ReadStatAsync(service, "gauges", name, cancellationToken);

    private async Task<long> ReadStatAsync(
        string service,
        string section,
        string name,
        CancellationToken cancellationToken)
    {
        var client = _clients.All.Single(s => s.Name == service).Client;
        var stats  = await ExperimentClients.GetJsonAsync(client, "/admin/stats", cancellationToken);
        return stats[section]?[name]?.GetValue<long>() ?? 0;
    }

    private async Task<JsonObject> BuildSummaryAsync(
        Scenario scenario,
        DateTimeOffset startedAt,
        bool drained,
        PerMessageCounts perMessage,
        List<DateTimeOffset>? toxiproxyAccepted,
        CancellationToken cancellationToken)
    {
        var totals = new JsonObject();
        foreach (var (name, client) in _clients.All)
        {
            totals[name] = await ExperimentClients.GetJsonAsync(client, "/admin/stats", cancellationToken);
        }

        var groundTruth =
            await ExperimentClients.GetJsonAsync(_clients.OrdersApi, "/admin/groundtruth", cancellationToken);
        var deadLetters = await _clients.PeekDeadLettersAsync(cancellationToken);

        long GetCounter(string service, string name) => totals[service]?["counters"]?[name]?.GetValue<long>() ?? 0;

        long GetGauge(string service, string name) => totals[service]?["gauges"]?[name]?.GetValue<long>() ?? 0;

        long GetGroundTruth(string name) => groundTruth[name]?.GetValue<long>() ?? 0;

        var sent = GetCounter("producer", "sent");

        // -1 when the Worker doesn't poll the queue counts (on the emulator, which reports none).
        long? queueDeadLettered = GetGauge("worker", "queue_dead_lettered");

        double? ComputePerMessage(long? value) =>
            value is null || sent == 0 ? null : Math.Round((double)value.Value / sent, 3);

        // npgsqlConnectionsCreated and toxiproxyConnections are physical connections, so far fewer than
        // dbConnectionOpens, which counts opens the pool serves too. toxiproxy sees failed opens too, and two TCP
        // connections for an open Postgres rejects (Npgsql tries again without GSS encryption).
        var layers = new (string Name, long? Value)[]
        {
            ("serviceBusDeliveries", GetCounter("worker", "received")),
            ("httpAttemptsFromWorker", GetCounter("worker", "http_attempts")),
            ("httpRequestsAtApi", GetCounter("ordersapi", "orders_requests")),
            ("dbConnectionOpens",
                GetCounter("ordersapi", "db_connection_opened") + GetCounter("ordersapi", "db_connection_failed")),
            ("npgsqlConnectionsCreated", GetCounter("ordersapi", "npgsql_connection_created")),
            ("toxiproxyConnections", toxiproxyAccepted?.Count),
            ("dbCommands",
                GetCounter("ordersapi", "db_command_executed")
                + GetCounter("ordersapi", "db_command_failed")
                + GetCounter("ordersapi", "db_command_canceled")),
            ("efRetries", GetCounter("ordersapi", "ef_retries")),
            ("pgInsertCalls", GetGroundTruth("insertCalls")),
            ("rows", GetGroundTruth("rows")),
        };

        var attempts = new JsonObject
        {
            ["messagesSent"] = sent
        };
        foreach (var (name, value) in layers)
        {
            attempts[name] = new JsonObject
            {
                ["total"]      = value,
                ["perMessage"] = ComputePerMessage(value)
            };
        }

        return new JsonObject
        {
            ["scenario"]           = JsonSerializer.SerializeToNode(scenario, JsonDefaults.Options),
            ["startedAt"]          = startedAt,
            ["finishedAt"]         = DateTimeOffset.UtcNow,
            ["drained"]            = drained,
            ["attemptsPerMessage"] = attempts,
            ["hitMessages"]        = perMessage.Summarize(),
            ["recovery"]           = RecoveryTime.Compute(_samples, _faultRemovedAtSeconds),
            ["duplicates"] = new JsonObject
            {
                ["rows"]                   = GetGroundTruth("rows"),
                ["distinctMessages"]       = GetGroundTruth("distinctMessages"),
                ["messagesWithDuplicates"] = GetGroundTruth("messagesWithDuplicates"),
                ["duplicateRows"]          = GetGroundTruth("duplicateRows"),
                ["maxRowsPerMessage"]      = GetGroundTruth("maxRowsPerMessage"),
            },
            ["deadLettered"] = new JsonObject
            {
                ["fromQueueRuntimeProperties"] = queueDeadLettered >= 0 ? queueDeadLettered : null,
                ["fromPeekingDlq"]             = deadLetters.Count,
            },
            ["totals"]      = totals,
            ["groundTruth"] = groundTruth,
        };
    }

    // Pages from the first sequence number each time, since a receiver otherwise goes on from where it last peeked.
    private static async Task<long> CountByPeekingAsync(
        ServiceBusReceiver receiver,
        CancellationToken cancellationToken)
    {
        long count = 0;
        long next  = 0;
        while (true)
        {
            var page = await receiver.PeekMessagesAsync(250, next, cancellationToken);
            if (page.Count == 0)
            {
                return count;
            }

            count += page.Count;
            next  =  page[^1].SequenceNumber + 1;
        }
    }

    private static string BuildStartPath(double rate, int? count) =>
        $"/start?rate={rate.ToString(CultureInfo.InvariantCulture)}" + (count is { } c ? $"&count={c}" : "");

    private static string Redact(string connectionString) =>
        string.Join(
            ';',
            connectionString.Split(';')
                .Select(p =>
                    p.StartsWith("SharedAccessKey=", StringComparison.OrdinalIgnoreCase) ? "SharedAccessKey=***" : p));

    private static async Task<JsonNode?> TryGetJsonAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ExperimentClients.GetJsonAsync(client, path, cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static async Task<bool> WaitUntilAsync(
        Func<CancellationToken, Task<bool>> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            try
            {
                if (await condition(cancellationToken))
                {
                    return true;
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // A service that is not answering just means "not yet".
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return false;
    }

    private static void Log(string message) => Console.WriteLine($"[{DateTimeOffset.Now:HH:mm:ss}] {message}");
}