namespace OrdersApi.Instrumentation;

internal sealed class CountingConnectionInterceptor : DbConnectionInterceptor
{
    private readonly ExperimentMetrics _metrics;

    public CountingConnectionInterceptor(ExperimentMetrics metrics)
    {
        _metrics = metrics;
    }

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        _metrics.Add("db_connection_opened");

    public override Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _metrics.Add("db_connection_opened");
        return Task.CompletedTask;
    }

    public override void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData) =>
        _metrics.Add("db_connection_failed");

    public override Task ConnectionFailedAsync(
        DbConnection connection,
        ConnectionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _metrics.Add("db_connection_failed");
        return Task.CompletedTask;
    }
}