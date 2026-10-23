namespace Runner.Scenarios;

// One scenario file in scenarios/. Times are in seconds from the moment the producer starts.
internal sealed record Scenario
{
    public string Name        { get; init; } = "";
    public string Description { get; init; } = "";

    // Messages per second.
    public double Rate { get; init; } = 10;

    // Stop after this many messages (smoke). Null = keep sending until the send window ends.
    public int? MessageCount { get; init; }

    // Sending without a fault before the fault clock starts.
    public int WarmupSeconds { get; init; }

    public ScenarioFault Fault { get; init; } = new();

    // Seconds after warm-up when the fault is applied.
    public int FaultStartSeconds { get; init; }

    public int FaultDurationSeconds { get; init; }

    // Seconds the producer keeps sending after the fault is removed.
    public int SendAfterFaultSeconds { get; init; } = 30;

    // How long to wait for the queue to empty after the producer stops.
    public int DrainTimeoutSeconds { get; init; } = 600;

    // Optional rate changes, e.g. a spike: [{ "atSeconds": 90, "rate": 30 }].
    public List<RateChange> RateChanges { get; init; } = [];

    // Worker switches for this run only, e.g. { "MaxConcurrentCalls": 32 }; the body of PUT /admin/experiment. Null =
    // the Worker's startup switches. Anything set here is a departure from the defaults: say so in the description.
    public JsonObject? WorkerSwitches { get; init; }

    public int FaultAppliedAt => WarmupSeconds + FaultStartSeconds;

    public int FaultRemovedAt => FaultAppliedAt + FaultDurationSeconds;

    public int ProducerStopsAt => FaultRemovedAt + SendAfterFaultSeconds;

    public static Scenario Load(string path) =>
        JsonSerializer.Deserialize<Scenario>(File.ReadAllText(path), JsonDefaults.Options)
        ?? throw new InvalidOperationException($"Could not read {path}");
}