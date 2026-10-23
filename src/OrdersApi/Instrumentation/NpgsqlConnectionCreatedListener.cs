namespace OrdersApi.Instrumentation;

// Counts physical connections Npgsql creates, as opposed to db_connection_opened, which also counts opens the pool
// serves. Npgsql records create_time once per new connection that opened; a failed open records nothing. The "direct"
// data source (setup and ground truth) is left out, so only connections through toxiproxy are counted.
internal sealed class NpgsqlConnectionCreatedListener : IDisposable
{
    private const string MeterName      = "Npgsql";
    private const string InstrumentName = "db.client.connection.npgsql.create_time";
    private const string PoolNameTag    = "db.client.connection.pool.name";

    private readonly ExperimentMetrics _metrics;
    private readonly MeterListener     _listener = new();

    public NpgsqlConnectionCreatedListener(ExperimentMetrics metrics)
    {
        _metrics = metrics;

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument is { Meter.Name: MeterName, Name: InstrumentName })
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<double>(OnMeasurement);
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    private void OnMeasurement(
        Instrument instrument,
        double measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags,
        object? state)
    {
        foreach (var tag in tags)
        {
            if (tag is { Key: PoolNameTag, Value: OrdersDatabase.DirectDataSourceName })
            {
                return;
            }
        }

        _metrics.Add("npgsql_connection_created");
    }
}