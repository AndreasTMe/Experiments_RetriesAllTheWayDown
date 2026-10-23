namespace OrdersApi.FaultInjection;

internal sealed class FaultInjectionMiddleware
{
    private readonly RequestDelegate   _next;
    private readonly FaultState        _faultState;
    private readonly ExperimentMetrics _metrics;

    public FaultInjectionMiddleware(RequestDelegate next, FaultState faultState, ExperimentMetrics metrics)
    {
        _next       = next;
        _faultState = faultState;
        _metrics    = metrics;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!OrdersRequestCountingMiddleware.IsOrdersRequest(context))
        {
            await _next(context);
            return;
        }

        var settings = _faultState.Current;
        var slots    = _faultState.Slots;

        if (settings.ErrorRate > 0 && Random.Shared.NextDouble() < settings.ErrorRate)
        {
            _metrics.Add("fault_errors_injected");
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            return;
        }

        if (slots is null)
        {
            await _next(context);
            return;
        }

        // Waiting requests do not give up when the caller does: a struggling server keeps working on them.
        // That is why neither the wait nor the delay takes context.RequestAborted.
        _faultState.EnterQueue();
        await slots.WaitAsync();
        _faultState.LeaveQueue();
        try
        {
            await Task.Delay(settings.ServiceTimeMs);
            await _next(context);
        }
        finally
        {
            slots.Release();
        }
    }
}