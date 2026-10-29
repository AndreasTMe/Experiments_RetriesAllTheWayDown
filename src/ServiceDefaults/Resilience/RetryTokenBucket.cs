namespace ServiceDefaults.Resilience;

// Configuration 5, a retry budget. One bucket per layer per process, shared by every message: each first attempt adds
// RetryRatio of a token and each retry takes a whole one, so over time retries are at most RetryRatio of first
// attempts. It starts full, so up to Capacity retries go through before the ratio applies.
public sealed class RetryTokenBucket
{
    public const double RetryRatio = 0.1;
    public const double Capacity   = 10;

    private readonly Lock   _lock   = new();
    private          double _tokens = Capacity;

    public void RecordFirstAttempt()
    {
        lock (_lock)
        {
            _tokens = Math.Min(Capacity, _tokens + RetryRatio);
        }
    }

    public bool TryTakeRetry()
    {
        lock (_lock)
        {
            if (_tokens < 1)
            {
                return false;
            }

            _tokens--;
            return true;
        }
    }
}