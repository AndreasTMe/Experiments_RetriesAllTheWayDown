namespace Runner.Commands;

internal static class FaultsCommand
{
    public const string Usage = """
                                Runner faults status
                                Runner faults postgres-down | postgres-up
                                Runner faults latency <ms> | timeout <ms> | reset-peer <ms>
                                Runner faults clear                          remove toxics, enable the proxy, turn API faults off
                                Runner faults api <errorRate> [<capacity> <serviceTimeMs>]
                                Runner faults api-off
                                """;

    public static async Task<int> RunAsync(
        ExperimentClients clients,
        string[] args,
        CancellationToken cancellationToken)
    {
        var proxy     = clients.Settings.Proxy;
        var toxiproxy = clients.Toxiproxy;

        switch (args.FirstOrDefault())
        {
            case "status":
                Console.WriteLine($"toxiproxy {await toxiproxy.GetVersionAsync(cancellationToken)}");
                Console.WriteLine(
                    (await toxiproxy.GetProxyAsync(proxy, cancellationToken))?.ToJsonString(JsonDefaults.Options));
                Console.WriteLine(
                    "api faults: "
                    + (await ExperimentClients.GetJsonAsync(clients.OrdersApi, "/admin/faults", cancellationToken))
                    .ToJsonString());
                return 0;
            case "postgres-down":
                await toxiproxy.DisableProxyAsync(proxy, cancellationToken);
                break;
            case "postgres-up":
                await toxiproxy.EnableProxyAsync(proxy, cancellationToken);
                break;
            case "latency" when args.Length == 2:
                await toxiproxy.AddLatencyAsync(
                    proxy,
                    int.Parse(args[1], CultureInfo.InvariantCulture),
                    cancellationToken);
                break;
            case "timeout" when args.Length == 2:
                await toxiproxy.AddTimeoutAsync(
                    proxy,
                    int.Parse(args[1], CultureInfo.InvariantCulture),
                    cancellationToken);
                break;
            case "reset-peer" when args.Length == 2:
                await toxiproxy.AddResetPeerAsync(
                    proxy,
                    int.Parse(args[1], CultureInfo.InvariantCulture),
                    cancellationToken);
                break;
            case "clear":
                await toxiproxy.ResetAsync(cancellationToken);
                await clients.ClearApiFaultsAsync(cancellationToken);
                break;
            case "api" when args.Length is 2 or 4:
                await clients.SetApiFaultsAsync(
                    double.Parse(args[1], CultureInfo.InvariantCulture),
                    args.Length == 4 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 0,
                    args.Length == 4 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 0,
                    cancellationToken);
                break;
            case "api-off":
                await clients.ClearApiFaultsAsync(cancellationToken);
                break;
            default:
                Console.WriteLine(Usage);
                return 2;
        }

        Console.WriteLine("ok");
        return 0;
    }
}