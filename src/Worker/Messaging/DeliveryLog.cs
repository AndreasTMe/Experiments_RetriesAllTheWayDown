namespace Worker.Messaging;

// Keeps the most recent deliveries and processor errors for the emulator check.
internal sealed class DeliveryLog
{
    private const int MaxEntries = 50_000;

    private readonly ConcurrentQueue<DeliveryRecord>       _deliveries = new();
    private readonly ConcurrentQueue<ProcessorErrorRecord> _errors     = new();

    public void RecordDelivery(string messageId, int deliveryCount)
    {
        _deliveries.Enqueue(new DeliveryRecord(messageId, deliveryCount, DateTimeOffset.UtcNow));
        while (_deliveries.Count > MaxEntries && _deliveries.TryDequeue(out _))
        {
        }
    }

    public void RecordError(string errorSource, string reason, string message)
    {
        _errors.Enqueue(new ProcessorErrorRecord(errorSource, reason, message, DateTimeOffset.UtcNow));
        while (_errors.Count > MaxEntries && _errors.TryDequeue(out _))
        {
        }
    }

    public IEnumerable<DeliveryRecord> GetDeliveries(string? messageId) =>
        messageId is null ? _deliveries.ToArray() : _deliveries.Where(d => d.MessageId == messageId).ToArray();

    public IEnumerable<ProcessorErrorRecord> GetErrors() => _errors.ToArray();

    public void Clear()
    {
        _deliveries.Clear();
        _errors.Clear();
    }
}