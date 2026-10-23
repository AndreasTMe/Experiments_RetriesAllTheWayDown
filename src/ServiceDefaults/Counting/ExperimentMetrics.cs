namespace ServiceDefaults.Counting;

// Counts things twice: once on the "Experiment" meter (for the dashboard) and once
// in a plain Interlocked total (for GET /admin/stats, which the Runner samples).
// Add also counts per message when CurrentMessageId is set (for GET /admin/stats/messages).
public sealed class ExperimentMetrics
{
    private sealed class RunningTotal
    {
        public long value;
    }

    public static readonly Meter Meter = new("Experiment");

    // The message handled in this async flow. Set by the Worker's handler and OrdersApi's request middleware, and it
    // flows on into HttpClient retries, EF retries and RequestAborted callbacks, so every attempt counts against it.
    private static readonly AsyncLocal<string?> CurrentMessage = new();

    private readonly ConcurrentDictionary<string, Counter<long>>                              _counters   = new();
    private readonly ConcurrentDictionary<string, RunningTotal>                               _totals     = new();
    private readonly ConcurrentDictionary<string, Func<long>>                                 _gauges     = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, RunningTotal>> _perMessage = new();

    public static string? CurrentMessageId
    {
        get => CurrentMessage.Value;
        set => CurrentMessage.Value = value;
    }

    public string Service { get; }

    public ExperimentMetrics(string service)
    {
        Service = service;
    }

    public void Add(string name, long value = 1, params KeyValuePair<string, object?>[] tags)
    {
        var counter = _counters.GetOrAdd(name, n => Meter.CreateCounter<long>($"experiment.{Service}.{n}"));
        counter.Add(value, tags);
        Interlocked.Add(ref _totals.GetOrAdd(name, _ => new RunningTotal()).value, value);

        if (CurrentMessageId is { Length: > 0 } messageId)
        {
            var counts = _perMessage.GetOrAdd(messageId, _ => new ConcurrentDictionary<string, RunningTotal>());
            Interlocked.Add(ref counts.GetOrAdd(name, _ => new RunningTotal()).value, value);
        }
    }

    // Adds to the stats total only. Used for per-tag breakdowns of a counter already on the meter.
    public void AddTotal(string name, long value = 1) =>
        Interlocked.Add(ref _totals.GetOrAdd(name, _ => new RunningTotal()).value, value);

    // Makes sure a total shows up in stats (as 0) before anything has been counted.
    public void Declare(params string[] names)
    {
        foreach (var name in names)
        {
            _totals.GetOrAdd(name, _ => new RunningTotal());
        }
    }

    public void RegisterGauge(string name, Func<long> read) => _gauges[name] = read;

    public long GetTotal(string name) =>
        _totals.TryGetValue(name, out var total) ? Interlocked.Read(ref total.value) : 0;

    public void Reset()
    {
        foreach (var total in _totals.Values)
        {
            Interlocked.Exchange(ref total.value, 0);
        }

        _perMessage.Clear();
    }

    public object TakeSnapshot() =>
        new
        {
            service   = Service,
            timestamp = DateTimeOffset.UtcNow,
            counters  = _totals.OrderBy(t => t.Key).ToDictionary(t => t.Key, t => Interlocked.Read(ref t.Value.value)),
            gauges    = _gauges.OrderBy(g => g.Key).ToDictionary(g => g.Key, g => g.Value()),
        };

    // Message id -> counter -> total, for every message counted since the last reset.
    public Dictionary<string, Dictionary<string, long>> TakeMessagesSnapshot() =>
        _perMessage.ToDictionary(
            m => m.Key,
            m => m.Value.OrderBy(c => c.Key).ToDictionary(c => c.Key, c => Interlocked.Read(ref c.Value.value)));
}