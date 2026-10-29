namespace OrdersApi.Instrumentation;

// Same defaults as the strategy EnableRetryOnFailure() creates (6 retries, 30 s max delay); it counts retries and,
// with a retry budget, only retries while the budget has a token.
internal sealed class RetryCountingExecutionStrategy : NpgsqlRetryingExecutionStrategy
{
    private readonly ExperimentMetrics _metrics;
    private readonly RetryTokenBucket? _retryBudget;

    public RetryCountingExecutionStrategy(
        ExecutionStrategyDependencies dependencies,
        ExperimentMetrics metrics,
        RetryTokenBucket? retryBudget) : base(dependencies)
    {
        _metrics     = metrics;
        _retryBudget = retryBudget;
    }

    protected override void OnFirstExecution()
    {
        _retryBudget?.RecordFirstAttempt();
        base.OnFirstExecution();
    }

    // Null ends the retries: EF throws RetryLimitExceededException with the last error inside.
    protected override TimeSpan? GetNextDelay(Exception lastException)
    {
        var delay = base.GetNextDelay(lastException);
        if (delay is null || _retryBudget is null || _retryBudget.TryTakeRetry())
        {
            return delay;
        }

        _metrics.Add("retry_budget_denied");
        return null;
    }

    protected override void OnRetry()
    {
        _metrics.Add("ef_retries");
        base.OnRetry();
    }
}