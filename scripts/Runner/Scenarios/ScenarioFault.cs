namespace Runner.Scenarios;

// kind: none | proxy-disabled | latency | timeout | reset-peer | api
internal sealed record ScenarioFault
{
    public string Kind          { get; init; } = "none";
    public int    LatencyMs     { get; init; }
    public int    TimeoutMs     { get; init; }
    public double ErrorRate     { get; init; }
    public int    Capacity      { get; init; }
    public int    ServiceTimeMs { get; init; }

    public async Task ApplyAsync(ExperimentClients clients, CancellationToken cancellationToken)
    {
        var proxy = clients.Settings.Proxy;
        switch (Kind)
        {
            case "none":
                break;
            case "proxy-disabled":
                await clients.Toxiproxy.DisableProxyAsync(proxy, cancellationToken);
                break;
            case "latency":
                await clients.Toxiproxy.AddLatencyAsync(proxy, LatencyMs, cancellationToken);
                break;
            case "timeout":
                await clients.Toxiproxy.AddTimeoutAsync(proxy, TimeoutMs, cancellationToken);
                break;
            case "reset-peer":
                await clients.Toxiproxy.AddResetPeerAsync(proxy, TimeoutMs, cancellationToken);
                break;
            case "api":
                await clients.SetApiFaultsAsync(ErrorRate, Capacity, ServiceTimeMs, cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"Unknown fault kind '{Kind}'");
        }
    }

    public async Task RemoveAsync(ExperimentClients clients, CancellationToken cancellationToken)
    {
        var proxy = clients.Settings.Proxy;
        switch (Kind)
        {
            case "proxy-disabled":
                await clients.Toxiproxy.EnableProxyAsync(proxy, cancellationToken);
                break;
            case "latency" or "timeout" or "reset-peer":
                await clients.Toxiproxy.RemoveAllToxicsAsync(proxy, cancellationToken);
                break;
            case "api":
                await clients.ClearApiFaultsAsync(cancellationToken);
                break;
        }
    }
}