namespace OrdersApi.Instrumentation;

internal sealed class CountingCommandInterceptor : DbCommandInterceptor
{
    private readonly ExperimentMetrics _metrics;

    public CountingCommandInterceptor(ExperimentMetrics metrics)
    {
        _metrics = metrics;
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        _metrics.Add("db_command_executed");
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        _metrics.Add("db_command_executed");
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        _metrics.Add("db_command_executed");
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        _metrics.Add("db_command_executed");
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        _metrics.Add("db_command_executed");
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        object? result,
        CancellationToken cancellationToken = default)
    {
        _metrics.Add("db_command_executed");
        return ValueTask.FromResult(result);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData) =>
        _metrics.Add("db_command_failed");

    public override Task CommandFailedAsync(
        DbCommand command,
        CommandErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _metrics.Add("db_command_failed");
        return Task.CompletedTask;
    }

    public override void CommandCanceled(DbCommand command, CommandEndEventData eventData) =>
        _metrics.Add("db_command_canceled");

    public override Task CommandCanceledAsync(
        DbCommand command,
        CommandEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _metrics.Add("db_command_canceled");
        return Task.CompletedTask;
    }
}