namespace OrdersApi.Instrumentation;

// Same defaults as the strategy EnableRetryOnFailure() creates (6 retries, 30 s max delay); it only counts retries.
internal sealed class RetryCountingExecutionStrategy : NpgsqlRetryingExecutionStrategy
{
    private readonly ExperimentMetrics _metrics;

    public RetryCountingExecutionStrategy(ExecutionStrategyDependencies dependencies, ExperimentMetrics metrics) : base(
        dependencies)
    {
        _metrics = metrics;
    }

    protected override void OnRetry()
    {
        _metrics.Add("ef_retries");
        base.OnRetry();
    }
}