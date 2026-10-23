namespace ServiceDefaults.Configuration;

// The Experiment:* switches. The AppHost sets them on every service as Experiment__* environment variables.
// Each service only reads the ones that apply to it.
public sealed record ExperimentSettings
{
    public bool HttpRetries        { get; init; } = true;
    public bool DbRetries          { get; init; } = true;
    public bool PassCancellation   { get; init; }
    public bool AlwaysThrow        { get; init; }
    public int  HoldMessageMs      { get; init; }
    public bool DisableLockRenewal { get; init; }

    // The SDK default. Only fault D raises it, from its scenario file: with 1 request in flight the API never queues.
    public int MaxConcurrentCalls { get; init; } = 1;

    // Used by the AppHost for the queue; here so /admin/config reports it. Fixed when the queue is created.
    public int MaxDeliveryCount { get; init; } = 10;
}