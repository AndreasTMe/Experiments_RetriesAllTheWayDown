namespace OrdersApi.Instrumentation;

// Counts /orders requests, responses by status code, and requests still running after the caller went away.
internal sealed class OrdersRequestCountingMiddleware
{
    private readonly RequestDelegate   _next;
    private readonly ExperimentMetrics _metrics;

    public OrdersRequestCountingMiddleware(RequestDelegate next, ExperimentMetrics metrics)
    {
        _next    = next;
        _metrics = metrics;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!IsOrdersRequest(context))
        {
            await _next(context);
            return;
        }

        // Everything this request causes counts against its message, including EF retries after the caller has gone.
        ExperimentMetrics.CurrentMessageId = context.Request.Headers["x-message-id"].ToString();
        _metrics.Add("orders_requests");

        var finished = 0;
        await using var registration = context.RequestAborted.Register(() =>
        {
            if (Volatile.Read(ref finished) == 0)
            {
                _metrics.Add("orders_caller_gone_while_running");
            }
        });

        var statusCode = 500;
        try
        {
            await _next(context);
            statusCode = context.Response.StatusCode;
        }
        finally
        {
            Volatile.Write(ref finished, 1);
            _metrics.Add("orders_responses", 1, new KeyValuePair<string, object?>("status_code", statusCode));
            _metrics.AddTotal($"orders_responses_{statusCode}");
        }
    }

    public static bool IsOrdersRequest(HttpContext context) =>
        HttpMethods.IsPost(context.Request.Method)
        && context.Request.Path.Equals("/orders", StringComparison.OrdinalIgnoreCase);
}