namespace OrdersApi.FaultInjection;

internal sealed class FaultState
{
    private readonly Lock _lock = new();
    private          long _queued;

    public FaultSettings Current { get; private set; } = new();

    public SemaphoreSlim? Slots { get; private set; }

    public long Queued => Interlocked.Read(ref _queued);

    public void Set(FaultSettings settings)
    {
        lock (_lock)
        {
            Current = settings;
            Slots   = settings.Capacity > 0 ? new SemaphoreSlim(settings.Capacity, settings.Capacity) : null;
        }
    }

    public void EnterQueue() => Interlocked.Increment(ref _queued);

    public void LeaveQueue() => Interlocked.Decrement(ref _queued);
}