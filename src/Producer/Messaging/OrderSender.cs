namespace Producer.Messaging;

internal sealed class OrderSender : IAsyncDisposable
{
    private readonly ServiceBusSender     _sender;
    private readonly ExperimentMetrics    _metrics;
    private readonly ActivitySource       _activitySource;
    private readonly ILogger<OrderSender> _logger;

    private readonly Lock _lock = new();

    private CancellationTokenSource? _run;

    public OrderSender(
        ServiceBusClient client,
        ExperimentMetrics metrics,
        ActivitySource activitySource,
        ILogger<OrderSender> logger)
    {
        _metrics        = metrics;
        _activitySource = activitySource;
        _logger         = logger;
        _sender         = client.CreateSender("orders");
    }

    public void Start(double rate, int? count)
    {
        lock (_lock)
        {
            _run?.Cancel();
            _run = new CancellationTokenSource();
            _    = SendAtRateAsync(rate, count, _run.Token);
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            _run?.Cancel();
            _run = null;
        }
    }

    public ValueTask DisposeAsync() => _sender.DisposeAsync();

    private async Task SendAtRateAsync(double rate, int? count, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Sending at {Rate}/s, count {Count}", rate, count?.ToString() ?? "unlimited");

        using var timer   = new PeriodicTimer(TimeSpan.FromSeconds(1 / rate));
        var       started = 0;

        try
        {
            do
            {
                // Each send runs on its own so a slow send does not lower the rate. Stopping the run does not
                // cancel sends already started, so every started send ends up counted as sent or send_failed.
                _ = SendOneAsync(CancellationToken.None);
                started++;
            } while ((count is null || started < count) && await timer.WaitForNextTickAsync(cancellationToken));
        }
        catch (OperationCanceledException)
        {
        }

        _logger.LogInformation("Stopped sending after {Started} messages", started);
    }

    private async Task SendOneAsync(CancellationToken cancellationToken)
    {
        var messageId = Guid.NewGuid().ToString();

        using var activity = _activitySource.StartActivity("send order", ActivityKind.Producer);
        activity?.SetTag("experiment.message_id", messageId);

        var body = new
        {
            orderId = Guid.NewGuid(),
            amount  = Math.Round(Random.Shared.NextDouble() * 100, 2)
        };
        var message = new ServiceBusMessage(JsonSerializer.SerializeToUtf8Bytes(body))
        {
            MessageId   = messageId,
            ContentType = "application/json",
        };

        try
        {
            await _sender.SendMessageAsync(message, cancellationToken);
            _metrics.Add("sent");
        }
        catch (Exception ex)
        {
            _metrics.Add("send_failed");
            _logger.LogWarning(ex, "Send failed for {MessageId}", messageId);
        }
    }
}