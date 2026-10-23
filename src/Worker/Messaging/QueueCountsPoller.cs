namespace Worker.Messaging;

// Reads the queue's active and dead-letter counts once a second through the management API, for a real namespace.
// The emulator's reply has no counts (both would read 0), so on the emulator it doesn't poll and both stay -1, not
// available; the Runner counts dead letters by peeking instead. Peeking the queue here every second slowed the worker
// down about 3 times in fault B, so it doesn't.
internal sealed class QueueCountsPoller : BackgroundService
{
    private readonly IConfiguration             _configuration;
    private readonly QueueCounts                _counts;
    private readonly ExperimentMetrics          _metrics;
    private readonly ILogger<QueueCountsPoller> _logger;

    public QueueCountsPoller(
        IConfiguration configuration,
        QueueCounts counts,
        ExperimentMetrics metrics,
        ILogger<QueueCountsPoller> logger)
    {
        _configuration = configuration;
        _counts        = counts;
        _metrics       = metrics;
        _logger        = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var connectionString = _configuration.GetConnectionString("servicebus-admin");
        if (string.IsNullOrEmpty(connectionString))
        {
            _logger.LogWarning("No servicebus-admin connection string; queue counts are not available");
            return;
        }

        if (connectionString.Contains("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogInformation("Service Bus emulator: it reports no queue counts, so they are not polled");
            return;
        }

        var       admin = new ServiceBusAdministrationClient(connectionString);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        do
        {
            try
            {
                QueueRuntimeProperties properties = await admin.GetQueueRuntimePropertiesAsync("orders", stoppingToken);
                _counts.Active       = properties.ActiveMessageCount;
                _counts.DeadLettered = properties.DeadLetterMessageCount;
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _metrics.Add("admin_errors");
                _logger.LogDebug(ex, "Reading queue runtime properties failed");
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}