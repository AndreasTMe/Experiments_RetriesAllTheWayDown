var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

var experiment = builder.Configuration.GetSection("Experiment").Get<ExperimentSettings>() ?? new ExperimentSettings();

var metrics = new ExperimentMetrics("ordersapi");
metrics.Declare(
    "orders_requests",
    "orders_caller_gone_while_running",
    "fault_errors_injected",
    "db_connection_opened",
    "db_connection_failed",
    "npgsql_connection_created",
    "db_command_executed",
    "db_command_failed",
    "db_command_canceled",
    "ef_retries");
builder.Services.AddSingleton(metrics);
builder.Services.AddSingleton(experiment);
builder.Services.AddSingleton<FaultState>();
builder.Services.AddSingleton<NpgsqlConnectionCreatedListener>();

// The Aspire integration calls EnableRetryOnFailure() unless DisableRetry is set. When DbRetries is on we
// replace that strategy with a subclass of the same NpgsqlRetryingExecutionStrategy (same defaults) that counts retries.
builder.AddNpgsqlDbContext<OrdersDbContext>(
    "ordersdb",
    settings => settings.DisableRetry = !experiment.DbRetries,
    options =>
    {
        if (experiment.DbRetries)
        {
            options.UseNpgsql(npgsql =>
                npgsql.ExecutionStrategy(deps => new RetryCountingExecutionStrategy(deps, metrics)));
        }

        options.AddInterceptors(new CountingConnectionInterceptor(metrics), new CountingCommandInterceptor(metrics));
    });

// A second connection that goes straight to Postgres, bypassing toxiproxy. Used for setup and ground truth only.
// Named, so its pool can be told apart in Npgsql's metrics.
builder.Services.AddKeyedSingleton(
    "direct",
    (_, _) =>
        new NpgsqlDataSourceBuilder(
            builder.Configuration.GetConnectionString("ordersdb-direct")
            ?? throw new InvalidOperationException("Missing connection string ordersdb-direct"))
        {
            Name = OrdersDatabase.DirectDataSourceName
        }.Build());

var app = builder.Build();

// Resolved before the first connection opens, so no physical open goes uncounted.
app.Services.GetRequiredService<NpgsqlConnectionCreatedListener>();

var stopping = app.Lifetime.ApplicationStopping;
await OrdersDatabase.EnsureCreatedAsync(app.Services.GetRequiredKeyedService<NpgsqlDataSource>("direct"), stopping);
await OrdersDatabase.LogConnectionTargetAsync(app.Services, app.Logger, stopping);

app.MapDefaultEndpoints();

app.UseMiddleware<OrdersRequestCountingMiddleware>();
app.UseMiddleware<FaultInjectionMiddleware>();

app.MapPost(
    "/orders",
    async (HttpContext http, OrdersDbContext dbContext) =>
    {
        // RequestAborted fires when the caller (the Worker's HttpClient) gives up. By default we do not pass it on,
        // so the insert keeps going after the caller has gone.
        var cancellationToken = experiment.PassCancellation ? http.RequestAborted : CancellationToken.None;

        var order = new Order
        {
            MessageId = http.Request.Headers["x-message-id"].ToString(),
            DeliveryAttemptHeader =
                int.TryParse(http.Request.Headers["x-delivery-count"], out var count) ? count : null,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        dbContext.Orders.Add(order);
        await dbContext.SaveChangesAsync(cancellationToken);

        return Results.Ok(
            new
            {
                order.Id
            });
    });

app.MapGet("/admin/faults", (FaultState faultState) => Results.Json(faultState.Current));
app.MapPut(
    "/admin/faults",
    (FaultSettings settings, FaultState faultState) =>
    {
        faultState.Set(settings);
        return Results.Json(faultState.Current);
    });

app.MapGet(
    "/admin/groundtruth",
    async ([FromKeyedServices("direct")] NpgsqlDataSource direct, CancellationToken cancellationToken) =>
    Results.Json(await GroundTruthReader.ReadAsync(direct, cancellationToken)));
app.MapGet(
    "/admin/groundtruth/messages",
    async ([FromKeyedServices("direct")] NpgsqlDataSource direct, CancellationToken cancellationToken) =>
    Results.Json(await GroundTruthReader.ReadRowsPerMessageAsync(direct, cancellationToken)));
app.MapPost(
    "/admin/groundtruth/reset",
    async ([FromKeyedServices("direct")] NpgsqlDataSource direct, CancellationToken cancellationToken) =>
    {
        await GroundTruthReader.ResetAsync(direct, cancellationToken);
        return Results.Ok();
    });

var faultState = app.Services.GetRequiredService<FaultState>();
metrics.RegisterGauge("fault_queued", () => faultState.Queued);
app.MapExperimentMetricsEndpoints(metrics);

app.MapGet(
    "/admin/config",
    (IConfiguration config) => Results.Json(
        new
        {
            service = "ordersapi",
            experiment,
            images   = config.GetSection("Images").Get<Dictionary<string, string>>(),
            faults   = faultState.Current,
            database = OrdersDatabase.Describe(app.Services),
        }));

app.Run();