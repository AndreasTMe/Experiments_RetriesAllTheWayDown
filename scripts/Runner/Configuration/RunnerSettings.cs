namespace Runner.Configuration;

internal sealed record RunnerSettings
{
    public string Producer           { get; init; } = "http://localhost:5101";
    public string Worker             { get; init; } = "http://localhost:5102";
    public string OrdersApi          { get; init; } = "http://localhost:5103";
    public string Toxiproxy          { get; init; } = "http://localhost:8474";
    public string ToxiproxyContainer { get; init; } = "external-toxiproxy";
    public string ServiceBus         { get; init; } = "";
    public string ServiceBusAdmin    { get; init; } = "";
    public string Queue              { get; init; } = "orders";
    public string Proxy              { get; init; } = "postgres";

    public bool UsesEmulator => ServiceBus.Contains("UseDevelopmentEmulator=true", StringComparison.OrdinalIgnoreCase);

    public static RunnerSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        var settings = JsonSerializer.Deserialize<RunnerSettings>(File.ReadAllText(path), JsonDefaults.Options)
                       ?? new RunnerSettings();

        // The same variable that makes the AppHost use a real namespace. A namespace has one endpoint for both.
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__servicebus");
        return string.IsNullOrEmpty(connectionString)
            ? settings
            : settings with
            {
                ServiceBus = connectionString,
                ServiceBusAdmin = connectionString
            };
    }
}