namespace Worker.Messaging;

// Runs a ServiceBusProcessor on "orders" with SDK defaults, apart from the switches below. If the handler throws, the
// SDK abandons the message (AutoCompleteMessages is true by default), so Service Bus redelivers it.
internal sealed class OrdersQueueProcessor : IHostedService
{
    // The attempt number a rescheduled copy carries; the first delivery has none.
    private const string AttemptProperty = "experiment-attempt";

    private static readonly Histogram<int> DeliveryCountHistogram =
        ExperimentMetrics.Meter.CreateHistogram<int>("experiment.worker.delivery_count");

    // 5 s, doubling, at most 5 min: 5, 10, 20, 40, 80, 160, 300, 300, 300 s, about 20 min over 10 attempts.
    private static readonly TimeSpan FirstRedeliveryDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRedeliveryDelay   = TimeSpan.FromMinutes(5);

    private readonly ServiceBusClient              _client;
    private readonly ServiceBusSender              _sender;
    private readonly IServiceProvider              _services;
    private readonly ExperimentMetrics             _metrics;
    private readonly DeliveryLog                   _deliveryLog;
    private readonly ILogger<OrdersQueueProcessor> _logger;

    private readonly SemaphoreSlim             _restartLock = new(1, 1);
    private readonly RuntimeExperimentSettings _experiment;

    private ServiceBusProcessor? _processor;
    private long                 _inFlight;

    public long InFlight => Interlocked.Read(ref _inFlight);

    public ServiceBusProcessorOptions Options { get; private set; } = new();

    public OrdersQueueProcessor(
        ServiceBusClient client,
        IServiceProvider services,
        RuntimeExperimentSettings experiment,
        ExperimentMetrics metrics,
        DeliveryLog deliveryLog,
        ILogger<OrdersQueueProcessor> logger)
    {
        _client      = client;
        _sender      = client.CreateSender("orders");
        _services    = services;
        _experiment  = experiment;
        _metrics     = metrics;
        _deliveryLog = deliveryLog;
        _logger      = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var settings = _experiment.Current;
        var options = new ServiceBusProcessorOptions
        {
            MaxConcurrentCalls = settings.MaxConcurrentCalls
        };

        if (settings.DisableLockRenewal)
        {
            options.MaxAutoLockRenewalDuration = TimeSpan.Zero;
        }

        Options                        =  options;
        _processor                     =  _client.CreateProcessor("orders", options);
        _processor.ProcessMessageAsync += HandleMessageAsync;
        _processor.ProcessErrorAsync   += HandleErrorAsync;

        _logger.LogInformation(
            "Starting processor: MaxConcurrentCalls={MaxConcurrentCalls}, PrefetchCount={PrefetchCount}, AutoCompleteMessages={AutoComplete}, MaxAutoLockRenewalDuration={LockRenewal}, Experiment={Experiment}",
            options.MaxConcurrentCalls,
            options.PrefetchCount,
            options.AutoCompleteMessages,
            options.MaxAutoLockRenewalDuration,
            settings);

        await _processor.StartProcessingAsync(cancellationToken);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_processor is not null)
        {
            await _processor.StopProcessingAsync(cancellationToken);
            await _processor.DisposeAsync();
            _processor = null;
        }
    }

    // The token only covers waiting for another restart to finish. Once stopped, the processor is always started
    // again, so a caller that gives up halfway does not leave the Worker without a processor.
    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await _restartLock.WaitAsync(cancellationToken);
        try
        {
            await StopAsync(CancellationToken.None);
            await StartAsync(CancellationToken.None);
        }
        finally
        {
            _restartLock.Release();
        }
    }

    private async Task HandleMessageAsync(ProcessMessageEventArgs args)
    {
        var message  = args.Message;
        var settings = _experiment.Current;

        Activity.Current?.SetTag("experiment.message_id", message.MessageId);
        ExperimentMetrics.CurrentMessageId = message.MessageId;

        _metrics.Add("received", 1, new KeyValuePair<string, object?>("delivery_count", message.DeliveryCount));
        _metrics.AddTotal(
            message.DeliveryCount <= 10 ? $"received_delivery_{message.DeliveryCount}" : "received_delivery_over_10");
        DeliveryCountHistogram.Record(message.DeliveryCount);
        _deliveryLog.RecordDelivery(message.MessageId, message.DeliveryCount);

        Interlocked.Increment(ref _inFlight);
        try
        {
            if (settings.HoldMessageMs > 0)
            {
                await Task.Delay(settings.HoldMessageMs, args.CancellationToken);
            }

            if (settings.AlwaysThrow)
            {
                throw new InvalidOperationException("Experiment:AlwaysThrow is on");
            }

            // Typed clients are transient; take a fresh one per message so handler rotation works as usual.
            var ordersApi = _services.GetRequiredService<OrdersApiClient>();
            await ordersApi.CreateOrderAsync(message, args.CancellationToken);
            _metrics.Add("handler_succeeded");
        }
        catch (Exception exception) when (settings.DelayedRedelivery && !args.CancellationToken.IsCancellationRequested)
        {
            await RedeliverLaterAsync(args, settings, exception);
        }
        catch
        {
            _metrics.Add("abandoned");
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _inFlight);
        }
    }

    // Configuration 6. Instead of abandoning, which Service Bus redelivers at once, send a copy of the message
    // scheduled for later and let the original complete. At MaxDeliveryCount attempts the Worker dead-letters the
    // message itself, so the budget is the same 10 tries as with defaults. If scheduling fails, the handler throws
    // and the SDK abandons.
    private async Task RedeliverLaterAsync(
        ProcessMessageEventArgs args,
        ExperimentSettings settings,
        Exception exception)
    {
        var message = args.Message;
        var attempt = message.ApplicationProperties.TryGetValue(AttemptProperty, out var value)
            ? Convert.ToInt32(value)
            : 1;

        // The handler no longer throws, so HandleErrorAsync doesn't see the failure; record it the same way.
        _deliveryLog.RecordError(
            nameof(ServiceBusErrorSource.ProcessMessageCallback),
            exception.GetType().Name,
            exception.Message);

        if (attempt >= settings.MaxDeliveryCount)
        {
            await args.DeadLetterMessageAsync(
                message,
                "DelayedRedeliveryExhausted",
                $"Failed {attempt} attempts",
                args.CancellationToken);
            _metrics.Add("dead_lettered_by_worker");
            return;
        }

        var copy = new ServiceBusMessage(message);
        copy.ApplicationProperties[AttemptProperty] = attempt + 1;
        var deliverAt = DateTimeOffset.UtcNow + GetRedeliveryDelay(attempt);
        await _sender.ScheduleMessageAsync(copy, deliverAt, args.CancellationToken);
        _metrics.Add("rescheduled");
    }

    private Task HandleErrorAsync(ProcessErrorEventArgs args)
    {
        var reason = args.Exception is ServiceBusException sbe ? sbe.Reason.ToString() : args.Exception.GetType().Name;

        if (args.Exception is ServiceBusException { Reason: ServiceBusFailureReason.MessageLockLost })
        {
            _metrics.Add("lock_lost");
        }

        if (args.ErrorSource == ServiceBusErrorSource.Complete)
        {
            _metrics.Add("complete_failed");
        }
        else if (args.ErrorSource == ServiceBusErrorSource.Abandon)
        {
            _metrics.Add("abandon_failed");
        }
        else if (args.ErrorSource != ServiceBusErrorSource.ProcessMessageCallback)
        {
            _metrics.Add("other_errors");
        }

        _deliveryLog.RecordError(args.ErrorSource.ToString(), reason, args.Exception.Message);

        // The user callback failing is the normal path here, so keep it short.
        if (args.ErrorSource == ServiceBusErrorSource.ProcessMessageCallback)
        {
            _logger.LogDebug("Handler failed: {Message}", args.Exception.Message);
        }
        else
        {
            _logger.LogWarning(
                "Processor error from {ErrorSource} ({Reason}): {Message}",
                args.ErrorSource,
                reason,
                args.Exception.Message);
        }

        return Task.CompletedTask;
    }

    private static TimeSpan GetRedeliveryDelay(int attempt) =>
        TimeSpan.FromTicks(Math.Min(FirstRedeliveryDelay.Ticks << (attempt - 1), MaxRedeliveryDelay.Ticks));
}