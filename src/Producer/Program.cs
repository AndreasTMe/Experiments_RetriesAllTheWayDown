var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureServiceBusClient("servicebus");

var metrics = new ExperimentMetrics("producer");
metrics.Declare("sent", "send_failed");
builder.Services.AddSingleton(metrics);
builder.Services.AddSingleton(new ActivitySource(builder.Environment.ApplicationName));
builder.Services.AddSingleton<OrderSender>();

var app = builder.Build();

app.MapDefaultEndpoints();
app.MapExperimentMetricsEndpoints(metrics);

// Sends `count` messages (or until /stop) at `rate` messages per second. A new /start replaces the current run.
app.MapPost(
    "/start",
    (double rate, int? count, OrderSender sender) =>
    {
        if (rate <= 0)
        {
            return Results.BadRequest("rate must be > 0");
        }

        sender.Start(rate, count);
        return Results.Ok(
            new
            {
                rate,
                count
            });
    });

app.MapPost(
    "/stop",
    (OrderSender sender) =>
    {
        sender.Stop();
        return Results.Ok();
    });

app.MapGet(
    "/admin/config",
    (IConfiguration config) => Results.Json(
        new
        {
            service         = "producer",
            experiment      = config.GetSection("Experiment").Get<ExperimentSettings>() ?? new ExperimentSettings(),
            images          = config.GetSection("Images").Get<Dictionary<string, string>>(),
            serviceBusRetry = new ServiceBusClientOptions().RetryOptions,
        }));

app.Run();