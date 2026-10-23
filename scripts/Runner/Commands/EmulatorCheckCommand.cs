namespace Runner.Commands;

// Checks that the emulator behaves the way the experiment assumes. Prints PASS or FAIL per check.
internal sealed class EmulatorCheckCommand
{
    private sealed record ObservedDelivery(int DeliveryCount, DateTimeOffset ReceivedAt);

    private const           int      ExpectedMaxDeliveryCount = 10;
    private static readonly TimeSpan ExpectedLockDuration     = TimeSpan.FromMinutes(1);

    private readonly ExperimentClients _clients;

    public EmulatorCheckCommand(ExperimentClients clients)
    {
        _clients = clients;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var results = new List<(string Name, bool Pass, string Detail)>();

        await PrepareAsync(cancellationToken);
        results.Add(await CheckQueuePropertiesAsync(cancellationToken));

        await PrepareAsync(cancellationToken);
        results.Add(await CheckMaxDeliveryCountAsync(cancellationToken));

        await PrepareAsync(cancellationToken);
        results.Add(await CheckLockLostAsync(cancellationToken));

        await RestoreWorkerAsync(cancellationToken);
        await _clients.PurgeQueueAsync(cancellationToken);

        Console.WriteLine();
        Console.WriteLine("Emulator check results");
        foreach (var (name, pass, detail) in results)
        {
            Console.WriteLine($"  {(pass ? "PASS" : "FAIL")}  {name}: {detail}");
        }

        return results.All(r => r.Pass) ? 0 : 1;
    }

    private async Task PrepareAsync(CancellationToken cancellationToken)
    {
        await RestoreWorkerAsync(cancellationToken);
        await ExperimentClients.PostAsync(_clients.Producer, "/stop", cancellationToken);
        await _clients.PurgeQueueAsync(cancellationToken);
        await ExperimentClients.PostAsync(_clients.Worker, "/admin/reset", cancellationToken);
    }

    private async Task RestoreWorkerAsync(CancellationToken cancellationToken)
    {
        using var response = await _clients.Worker.DeleteAsync("/admin/experiment", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // 1. Read the queue properties back through the management API.
    private async Task<(string, bool, string)> CheckQueuePropertiesAsync(CancellationToken cancellationToken)
    {
        const string name = "1 queue properties";
        Console.WriteLine($"== {name}");
        Console.WriteLine(
            $"   Configured in AppHost: MaxDeliveryCount={ExpectedMaxDeliveryCount}, LockDuration={ExpectedLockDuration}, "
            + "DeadLetteringOnMessageExpiration=False, RequiresSession=False, RequiresDuplicateDetection=False");

        QueueProperties queue;
        try
        {
            var admin = new ServiceBusAdministrationClient(_clients.Settings.ServiceBusAdmin);
            queue = await admin.GetQueueAsync(_clients.Settings.Queue, cancellationToken);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine(
                $"   ServiceBusAdministrationClient did not work against the emulator: {ex.GetType().Name}: {ex.Message}");
            return (name, false, "could not read properties back; only the configured values above are known");
        }

        Console.WriteLine(
            $"   Read back: MaxDeliveryCount={queue.MaxDeliveryCount}, LockDuration={queue.LockDuration}, "
            + $"DeadLetteringOnMessageExpiration={queue.DeadLetteringOnMessageExpiration}, RequiresSession={queue.RequiresSession}, "
            + $"RequiresDuplicateDetection={queue.RequiresDuplicateDetection}, DefaultMessageTimeToLive={queue.DefaultMessageTimeToLive}, "
            + $"DuplicateDetectionHistoryTimeWindow={queue.DuplicateDetectionHistoryTimeWindow}, EnableBatchedOperations={queue.EnableBatchedOperations}, "
            + $"AutoDeleteOnIdle={queue.AutoDeleteOnIdle}, ForwardTo={queue.ForwardTo ?? "(none)"}, Status={queue.Status}");

        var pass = queue.MaxDeliveryCount == ExpectedMaxDeliveryCount
                   && queue.LockDuration == ExpectedLockDuration
                   && !queue.DeadLetteringOnMessageExpiration
                   && !queue.RequiresSession
                   && !queue.RequiresDuplicateDetection;

        return (name, pass,
            $"MaxDeliveryCount={queue.MaxDeliveryCount}, LockDuration={queue.LockDuration}, "
            + $"DeadLetteringOnMessageExpiration={queue.DeadLetteringOnMessageExpiration}, RequiresSession={queue.RequiresSession}, "
            + $"RequiresDuplicateDetection={queue.RequiresDuplicateDetection}");
    }

    // 2. With AlwaysThrow, one message is delivered 10 times and then dead-lettered.
    private async Task<(string, bool, string)> CheckMaxDeliveryCountAsync(CancellationToken cancellationToken)
    {
        const string name = "2 max delivery count -> DLQ";
        Console.WriteLine($"== {name}");

        await ExperimentClients.PutAsync(
            _clients.Worker,
            "/admin/experiment",
            new
            {
                alwaysThrow = true
            },
            cancellationToken);
        var messageId = await SendTestMessageAsync(cancellationToken);

        ServiceBusReceivedMessage? deadLettered = null;
        var                        clock        = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromMinutes(5) && deadLettered is null)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            deadLettered = (await _clients.PeekDeadLettersAsync(cancellationToken))
                .FirstOrDefault(m => m.MessageId == messageId);
        }

        await RestoreWorkerAsync(cancellationToken);

        var deliveries = await GetDeliveriesAsync(messageId, cancellationToken);
        var counts     = deliveries.Select(d => d.DeliveryCount).ToList();
        Console.WriteLine($"   Delivery counts seen by the worker: [{string.Join(", ", counts)}]");

        for (var i = 1; i < deliveries.Count; i++)
        {
            Console.WriteLine(
                $"   delivery {deliveries[i].DeliveryCount}: {(deliveries[i].ReceivedAt - deliveries[i - 1].ReceivedAt).TotalMilliseconds:F0} ms after the previous one");
        }

        var gaps = deliveries.Zip(deliveries.Skip(1), (a, b) => (b.ReceivedAt - a.ReceivedAt).TotalMilliseconds)
            .ToList();
        var gapSummary = gaps.Count == 0
            ? "no gaps"
            : $"time between deliveries min {gaps.Min():F0} ms, avg {gaps.Average():F0} ms, max {gaps.Max():F0} ms";

        if (deadLettered is null)
        {
            Console.WriteLine("   Not found in the DLQ within 5 minutes");
        }
        else
        {
            Console.WriteLine(
                $"   In DLQ: DeadLetterReason={deadLettered.DeadLetterReason}, DeadLetterErrorDescription={deadLettered.DeadLetterErrorDescription}, DeliveryCount={deadLettered.DeliveryCount}");
        }

        var pass = counts.SequenceEqual(Enumerable.Range(1, ExpectedMaxDeliveryCount)) && deadLettered is not null;
        return (name, pass,
            $"counts [{string.Join(",", counts)}], in DLQ: {(deadLettered is null ? "no" : $"yes ({deadLettered.DeadLetterReason})")}; {gapSummary}");
    }

    // 3. Holding a message past its 1 minute lock with renewal off: completing fails with lock lost and it comes back.
    private async Task<(string, bool, string)> CheckLockLostAsync(CancellationToken cancellationToken)
    {
        const string name = "3 lock lost -> redelivery";
        Console.WriteLine($"== {name}");

        await ExperimentClients.PutAsync(
            _clients.Worker,
            "/admin/experiment",
            new
            {
                holdMessageMs      = 70_000,
                disableLockRenewal = true
            },
            cancellationToken);
        var errorsBefore = (await GetErrorsAsync(cancellationToken)).Count;
        var messageId    = await SendTestMessageAsync(cancellationToken);
        Console.WriteLine("   Holding the message for 70 s with lock renewal off (lock duration 1 minute)...");

        var                    clock      = Stopwatch.StartNew();
        List<ObservedDelivery> deliveries = [];
        while (clock.Elapsed < TimeSpan.FromMinutes(4))
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            deliveries = await GetDeliveriesAsync(messageId, cancellationToken);
            if (deliveries.Any(d => d.DeliveryCount >= 2))
            {
                break;
            }
        }

        // Stops the second hold early (the processor restarts) so the message can complete normally.
        await RestoreWorkerAsync(cancellationToken);

        var errors = (await GetErrorsAsync(cancellationToken)).Skip(errorsBefore).ToList();
        foreach (var error in errors)
        {
            Console.WriteLine(
                $"   processor error: source={error["errorSource"]}, reason={error["reason"]}, {error["message"]}");
        }

        var lockLostOnComplete = errors.Any(e => e["errorSource"]?.ToString() == nameof(ServiceBusErrorSource.Complete)
                                                 && e["reason"]?.ToString()
                                                 == nameof(ServiceBusFailureReason.MessageLockLost));
        var redelivered = deliveries.FirstOrDefault(d => d.DeliveryCount == 2);

        Console.WriteLine(
            $"   Delivery counts seen by the worker: [{string.Join(", ", deliveries.Select(d => d.DeliveryCount))}]");
        if (redelivered is not null && deliveries.Count > 0)
        {
            Console.WriteLine(
                $"   Redelivered {(redelivered.ReceivedAt - deliveries[0].ReceivedAt).TotalSeconds:F1} s after the first delivery");
        }

        var pass = lockLostOnComplete && redelivered is not null;
        return (name, pass,
            $"complete failed with MessageLockLost: {(lockLostOnComplete ? "yes" : "no")}, redelivered with delivery count 2: {(redelivered is null ? "no" : "yes")}");
    }

    private async Task<string> SendTestMessageAsync(CancellationToken cancellationToken)
    {
        await using var sender    = _clients.ServiceBus.CreateSender(_clients.Settings.Queue);
        var             messageId = $"emulator-check-{Guid.NewGuid()}";
        await sender.SendMessageAsync(
            new ServiceBusMessage(
                BinaryData.FromObjectAsJson(
                    new
                    {
                        orderId = Guid.NewGuid(),
                        amount  = 1.0
                    }))
            {
                MessageId   = messageId,
                ContentType = "application/json",
            },
            cancellationToken);
        Console.WriteLine($"   Sent {messageId}");
        return messageId;
    }

    private async Task<List<ObservedDelivery>> GetDeliveriesAsync(string messageId, CancellationToken cancellationToken)
    {
        var json = await ExperimentClients.GetJsonAsync(
            _clients.Worker,
            $"/admin/deliveries?messageId={Uri.EscapeDataString(messageId)}",
            cancellationToken);
        return json.AsArray()
            .Select(d => new ObservedDelivery(
                d!["deliveryCount"]!.GetValue<int>(),
                d["receivedAt"]!.GetValue<DateTimeOffset>()))
            .ToList();
    }

    private async Task<List<JsonNode>> GetErrorsAsync(CancellationToken cancellationToken) =>
        (await ExperimentClients.GetJsonAsync(_clients.Worker, "/admin/errors", cancellationToken))
        .AsArray()
        .Select(e => e!)
        .ToList();
}