namespace Worker.Clients;

// Sits inside the resilience handler, so it sees every attempt, retries included.
internal sealed class AttemptCountingHandler : DelegatingHandler
{
    private readonly ExperimentMetrics _metrics;

    public AttemptCountingHandler(ExperimentMetrics metrics)
    {
        _metrics = metrics;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        _metrics.Add("http_attempts");
        return await base.SendAsync(request, cancellationToken);
    }
}