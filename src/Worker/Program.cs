var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddAzureServiceBusClient("servicebus");

var experiment = new RuntimeExperimentSettings(
    builder.Configuration.GetSection("Experiment").Get<ExperimentSettings>() ?? new ExperimentSettings());

var metrics = new ExperimentMetrics("worker");
metrics.Declare(
    "received",
    "handler_succeeded",
    "abandoned",
    "rescheduled",
    "dead_lettered_by_worker",
    "complete_failed",
    "abandon_failed",
    "lock_lost",
    "other_errors",
    "http_attempts",
    "retry_budget_denied",
    "admin_errors");
builder.Services.AddSingleton(metrics);
builder.Services.AddSingleton(experiment);
builder.Services.AddSingleton<DeliveryLog>();
builder.Services.AddSingleton<QueueCounts>();
builder.Services.AddSingleton(new ActivitySource(builder.Environment.ApplicationName));

// The standard resilience handler comes from ServiceDefaults (ConfigureHttpClientDefaults).
// HttpRetries=false keeps the pipeline (timeouts, circuit breaker) but makes the retry strategy handle nothing.
if (!experiment.Initial.HttpRetries)
{
    builder.Services.ConfigureAll<HttpStandardResilienceOptions>(options =>
        options.Retry.ShouldHandle = _ => ValueTask.FromResult(false));
}

// TunedBreaker=true: a breaker that can trip at this traffic. During an outage the Worker sends about 3 attempts per
// 30 s (one message at a time, each attempt running to its 10 s timeout), so the default minimum of 100 requests in
// 30 s is only reached while the window still holds the successes from before the fault, and those keep the failure
// ratio under 10%. A minimum of 2 and a ratio of 50% trip on the failures alone; the break is 30 s, not 5 s.
if (experiment.Initial.TunedBreaker)
{
    builder.Services.ConfigureAll<HttpStandardResilienceOptions>(options =>
    {
        options.CircuitBreaker.FailureRatio      = 0.5;
        options.CircuitBreaker.MinimumThroughput = 2;
        options.CircuitBreaker.BreakDuration     = TimeSpan.FromSeconds(30);
    });
}

// RetryBudget=true: a retry goes ahead only if the shared bucket has a token. Every first attempt adds to it. The last
// attempt isn't checked, since it can't retry anyway.
if (experiment.Initial.RetryBudget)
{
    var retryBudget = new RetryTokenBucket();
    builder.Services.ConfigureAll<HttpStandardResilienceOptions>(options =>
    {
        var shouldHandle = options.Retry.ShouldHandle;
        var maxRetries   = options.Retry.MaxRetryAttempts;
        options.Retry.ShouldHandle = async args =>
        {
            if (args.AttemptNumber == 0)
            {
                retryBudget.RecordFirstAttempt();
            }

            if (!await shouldHandle(args) || args.AttemptNumber >= maxRetries)
            {
                return false;
            }

            if (retryBudget.TryTakeRetry())
            {
                return true;
            }

            metrics.Add("retry_budget_denied");
            return false;
        };
    });
}

builder.Services.AddTransient<AttemptCountingHandler>();
builder.Services.AddHttpClient<OrdersApiClient>(client => client.BaseAddress = new Uri("http://ordersapi"))
    .AddHttpMessageHandler<AttemptCountingHandler>();

builder.Services.AddSingleton<OrdersQueueProcessor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<OrdersQueueProcessor>());
builder.Services.AddHostedService<QueueCountsPoller>();

var app = builder.Build();

app.MapDefaultEndpoints();

var queueCounts = app.Services.GetRequiredService<QueueCounts>();
var processor   = app.Services.GetRequiredService<OrdersQueueProcessor>();
metrics.RegisterGauge("in_flight", () => processor.InFlight);
// Completed = the handler returned and the SDK did not report a failed Complete.
metrics.RegisterGauge("completed", () => metrics.GetTotal("handler_succeeded") - metrics.GetTotal("complete_failed"));
metrics.RegisterGauge("queue_active", () => queueCounts.Active);
metrics.RegisterGauge("queue_dead_lettered", () => queueCounts.DeadLettered);

var deliveryLog = app.Services.GetRequiredService<DeliveryLog>();
app.MapExperimentMetricsEndpoints(metrics, deliveryLog.Clear);

// Every delivery (message id, delivery count, time) and every processor error, newest last.
app.MapGet("/admin/deliveries", (string? messageId) => Results.Json(deliveryLog.GetDeliveries(messageId)));
app.MapGet("/admin/errors", () => Results.Json(deliveryLog.GetErrors()));

// Runtime override of the Experiment switches, used by `Runner emulator-check` and by scenarios that set
// workerSwitches. Restarts the processor.
app.MapGet("/admin/experiment", () => Results.Json(experiment.Current));
app.MapPut(
    "/admin/experiment",
    async (ExperimentSettings settings, CancellationToken cancellationToken) =>
    {
        // Only the Worker's own switches change at runtime; HttpRetries, TunedBreaker and RetryBudget are fixed when
        // the HttpClient is built.
        experiment.Current = experiment.Initial with
        {
            AlwaysThrow = settings.AlwaysThrow,
            HoldMessageMs = settings.HoldMessageMs,
            DisableLockRenewal = settings.DisableLockRenewal,
            MaxConcurrentCalls = settings.MaxConcurrentCalls,
        };
        await processor.RestartAsync(cancellationToken);
        return Results.Json(experiment.Current);
    });
app.MapDelete(
    "/admin/experiment",
    async (CancellationToken cancellationToken) =>
    {
        experiment.Current = experiment.Initial;
        await processor.RestartAsync(cancellationToken);
        return Results.Json(experiment.Current);
    });

app.MapGet(
    "/admin/config",
    (IConfiguration config) => Results.Json(
        new
        {
            service         = "worker",
            experiment      = experiment.Current,
            images          = config.GetSection("Images").Get<Dictionary<string, string>>(),
            processor       = processor.Options,
            serviceBusRetry = new ServiceBusClientOptions().RetryOptions,
        }));

app.Run();