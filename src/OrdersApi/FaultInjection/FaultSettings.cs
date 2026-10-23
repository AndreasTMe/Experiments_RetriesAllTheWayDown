namespace OrdersApi.FaultInjection;

internal sealed record FaultSettings
{
    // Fraction of /orders requests answered with 500 straight away.
    public double ErrorRate { get; init; }

    // Brownout: at most Capacity requests at a time; each holds its slot for ServiceTimeMs. 0 = off.
    public int Capacity { get; init; }

    public int ServiceTimeMs { get; init; }
}