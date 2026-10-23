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

    public static RunnerSettings Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        return JsonSerializer.Deserialize<RunnerSettings>(File.ReadAllText(path), JsonDefaults.Options)
               ?? new RunnerSettings();
    }
}