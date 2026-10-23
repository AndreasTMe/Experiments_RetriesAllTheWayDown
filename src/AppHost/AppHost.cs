var builder = DistributedApplication.CreateBuilder(args);

// Dashboard names: external-* for the containers (children grouped under them), api-*/worker-* for the services,
// and the Runner commands under test-runner. The services keep their old connection names, so their code does not change.

// Every container image is pinned to an exact tag. These are also reported by /admin/config.
var images = new Dictionary<string, string>
{
    ["PgWeb"]                 = "docker.io/sosedoff/pgweb:0.16.2",
    ["Postgres"]              = "docker.io/library/postgres:18.3",
    ["ServiceBusEmulator"]    = "mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.0",
    ["ServiceBusEmulatorSql"] = "mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04",
    ["Toxiproxy"]             = "ghcr.io/shopify/toxiproxy:2.13.0",
};

// --- Service Bus emulator with queue "orders" ---------------------------------------------

var serviceBus = builder.AddAzureServiceBus("external-servicebus")
    .RunAsEmulator(emulator => emulator
        .WithImageTag("2.0.0")
        .WithHostPort(5672)
        .WithEndpoint("emulatorhealth", e => e.Port = 5300));

// Experiment:MaxDeliveryCount, 10 by default. 1 turns Service Bus redelivery effectively off: the first abandon
// dead-letters the message. Service Bus has no 0.
var maxDeliveryCount = int.Parse(
    builder.Configuration["Experiment:MaxDeliveryCount"] ?? "10",
    CultureInfo.InvariantCulture);
if (maxDeliveryCount < 1)
{
    throw new InvalidOperationException($"Experiment:MaxDeliveryCount must be at least 1, not {maxDeliveryCount}");
}

serviceBus.AddServiceBusQueue("orders")
    .WithProperties(queue =>
    {
        queue.MaxDeliveryCount                 = maxDeliveryCount;
        queue.LockDuration                     = TimeSpan.FromMinutes(1);
        queue.DeadLetteringOnMessageExpiration = false;
        queue.RequiresSession                  = false;
        queue.RequiresDuplicateDetection       = false;
    });

// RunAsEmulator adds a SQL Server container tagged "2022-latest". Pin it.
var emulatorSql = builder.Resources.OfType<ContainerResource>()
    .Single(r => r.Name == $"{serviceBus.Resource.Name}-mssql");
builder.CreateResourceBuilder(emulatorSql)
    .WithImageTag("2022-CU27-ubuntu-22.04")
    .WithParentRelationship(serviceBus);

// The emulator answers management (ServiceBusAdministrationClient) requests on the same port as its health endpoint.
var serviceBusAdmin = builder.AddConnectionString(
        "servicebus-admin",
        ReferenceExpression.Create(
            $"Endpoint=sb://{serviceBus.GetEndpoint("emulatorhealth").Property(EndpointProperty.HostAndPort)};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=SAS_KEY_VALUE;UseDevelopmentEmulator=true;"))
    .WithParentRelationship(serviceBus);

// --- Postgres, reached through toxiproxy --------------------------------------------------

// Declared explicitly so it is a resource in the model. The password AddPostgres generates on its own is not,
// and the "ordersdb" connection string below would then wait for it forever.
var postgresPassword = builder.AddParameter(
    "postgres-password",
    new GenerateParameterDefault(),
    secret: true,
    persist: true);

var postgres = builder.AddPostgres("external-postgres", password: postgresPassword)
    .WithImageTag("18.3")
    .WithArgs("-c", "shared_preload_libraries=pg_stat_statements");

// A web UI for Postgres. It connects directly, not through toxiproxy.
IResourceBuilder<PgWebContainerResource>? pgweb = null;
postgres.WithPgWeb(container => pgweb = container.WithImageTag("0.16.2"), containerName: "pgweb");
pgweb!.WithParentRelationship(postgres);
postgresPassword.WithParentRelationship(postgres);

// Named "ordersdb-direct": the direct connection that bypasses toxiproxy (setup and ground truth only).
var ordersDb = postgres.AddDatabase("ordersdb-direct", "orders");

// Proxy "postgres" is created at startup from toxiproxy.json: it listens on 15432 and forwards to
// external-postgres:5432 (containers reach each other by resource name).
var toxiproxy = builder.AddContainer("external-toxiproxy", "ghcr.io/shopify/toxiproxy", "2.13.0")
    .WithBindMount("toxiproxy.json", "/config/toxiproxy.json", isReadOnly: true)
    .WithArgs("-host=0.0.0.0", "-config=/config/toxiproxy.json")
    .WithHttpEndpoint(port: 8474, targetPort: 8474, name: "api", isProxied: false)
    .WithEndpoint(port: 15432, targetPort: 15432, name: "pg", scheme: "tcp", isProxied: false)
    .WithHttpHealthCheck("/version", endpointName: "api")
    .WaitFor(postgres);

var toxiproxyPg = toxiproxy.GetEndpoint("pg");

// What OrdersApi uses for its DbContext: the toxiproxy listener, never Postgres itself.
var ordersDbViaToxiproxy = builder.AddConnectionString(
        "ordersdb",
        ReferenceExpression.Create(
            $"Host={toxiproxyPg.Property(EndpointProperty.Host)};Port={toxiproxyPg.Property(EndpointProperty.Port)};Username={postgres.Resource.UserNameReference};Password={postgres.Resource.PasswordParameter};Database=orders"))
    .WithParentRelationship(toxiproxy);

// --- Projects -----------------------------------------------------------------------------

var ordersApi = builder.AddProject<Projects.OrdersApi>("api-orders")
    .WithReference(ordersDbViaToxiproxy)
    .WithReference(ordersDb)
    .WaitFor(ordersDb)
    .WaitFor(toxiproxy);

// The Worker calls http://ordersapi, so the reference keeps that service name.
var worker = builder.AddProject<Projects.Worker>("worker-orders")
    .WithReference(serviceBus, "servicebus")
    .WithReference(serviceBusAdmin)
    .WithReference(ordersApi, "ordersapi")
    .WaitFor(serviceBus)
    .WaitFor(ordersApi);

var producer = builder.AddProject<Projects.Producer>("worker-producer")
    .WithReference(serviceBus, "servicebus")
    .WaitFor(serviceBus);

// --- Runner commands, started by hand from the dashboard -----------------------------------

// The Runner is not part of the app model: each resource under test-runner is a "dotnet run" of it that the dashboard
// starts on demand. The Runner still runs the same way from a terminal.
// scenario-* runs a whole measured experiment, applying and removing its fault on schedule. faults-* only applies or
// removes a fault and exits, for trying things by hand and for cleaning up after a scenario that was stopped.
var repoRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));

// The dashboard sorts top-level resources by type, then by name, and has no setting for the order. "Automation" sorts
// before AzureServiceBus, Container and Project, so the group sits at the top.
var testRunner = builder.AddResource(new DashboardGroupResource("test-runner"))
    .WithInitialState(
        new CustomResourceSnapshot
        {
            ResourceType = "Automation",
            State        = KnownResourceStates.NotStarted,
            Properties   = [],
        });

IEnumerable<(string Name, string[] Args)> runnerCommands = Directory
    .GetFiles(Path.Combine(repoRoot, "scenarios"), "*.json")
    .Select(Path.GetFileNameWithoutExtension)
    .Order()
    // --dev: runs started from the dashboard are for trying things, so they go to results/dev/, which git ignores.
    .Select(scenario => (Name: $"scenario-{scenario!.ToLowerInvariant()}", Args: new[]
    {
        scenario, "--dev"
    }))
    .Concat(
    [
        ("emulator-check", ["emulator-check"]),
        ("faults-status", ["faults", "status"]),
        ("faults-clear", ["faults", "clear"]),
        ("faults-postgres-down", ["faults", "postgres-down"]),
        ("faults-postgres-up", ["faults", "postgres-up"]),
    ]);

foreach (var (name, runnerArgs) in runnerCommands)
{
    builder.AddExecutable(
            name,
            "dotnet",
            repoRoot,
            ["run", "--project", Path.Combine("scripts", "Runner"), "--", .. runnerArgs])
        .WithParentRelationship(testRunner)
        .WithExplicitStart()
        .WaitFor(producer)
        .WaitFor(worker)
        .WaitFor(ordersApi);
}

// --- Experiment switches and image versions, passed down as environment variables ----------

var experiment = builder.Configuration.GetSection("Experiment");

foreach (var project in new[]
         {
             ordersApi, worker, producer
         })
{
    foreach (var setting in experiment.GetChildren())
    {
        project.WithEnvironment($"Experiment__{setting.Key}", setting.Value ?? "");
    }

    foreach (var (name, image) in images)
    {
        project.WithEnvironment($"Images__{name}", image);
    }
}

// The Service Bus client integration only emits traces when this switch is on.
worker.WithEnvironment("AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE", "true");
producer.WithEnvironment("AZURE_EXPERIMENTAL_ENABLE_ACTIVITY_SOURCE", "true");

builder.Build().Run();