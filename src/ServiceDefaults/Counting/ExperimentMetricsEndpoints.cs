namespace ServiceDefaults.Counting;

public static class ExperimentMetricsEndpoints
{
    // GET /admin/stats, GET /admin/stats/messages and POST /admin/reset.
    public static WebApplication MapExperimentMetricsEndpoints(
        this WebApplication app,
        ExperimentMetrics metrics,
        Action? onReset = null)
    {
        app.MapGet("/admin/stats", () => Results.Json(metrics.TakeSnapshot()));
        app.MapGet("/admin/stats/messages", () => Results.Json(metrics.TakeMessagesSnapshot()));
        app.MapPost(
            "/admin/reset",
            () =>
            {
                metrics.Reset();
                onReset?.Invoke();
                return Results.Ok();
            });
        return app;
    }
}