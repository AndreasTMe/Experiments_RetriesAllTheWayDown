namespace Runner.Clients;

// Reads toxiproxy's container log through the docker CLI. toxiproxy has no connection counter (its /metrics has bytes
// only), but at the default info level it logs one "Accepted client" line per TCP connection a proxy accepts. Its
// timestamps have second precision. Aspire adds a random suffix to container names, so the container is found by
// name prefix.
internal sealed class ToxiproxyLog
{
    private const string AcceptedClient = "Accepted client";

    private readonly string _containerPrefix;

    public ToxiproxyLog(string containerPrefix)
    {
        _containerPrefix = containerPrefix;
    }

    // The time of each connection the proxy accepted since `since`.
    public async Task<List<DateTimeOffset>> ReadAcceptedClientsAsync(
        string proxy,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var names = await RunDockerAsync(
            ["ps", "--filter", $"name={_containerPrefix}", "--format", "{{.Names}}"],
            cancellationToken);
        var container = SplitLines(names).Single();
        var log = await RunDockerAsync(
            ["logs", "--since", since.UtcDateTime.ToString("O"), container],
            cancellationToken);

        var accepted = new List<DateTimeOffset>();
        foreach (var line in SplitLines(log))
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            var entry = JsonNode.Parse(line);
            if (entry?["message"]?.GetValue<string>() == AcceptedClient
                && entry["name"]?.GetValue<string>() == proxy)
            {
                accepted.Add(DateTimeOffset.Parse(entry["time"]!.GetValue<string>(), CultureInfo.InvariantCulture));
            }
        }

        return accepted;
    }

    // stdout and stderr together: toxiproxy logs to stderr, and docker logs passes each stream on as it was.
    private static async Task<string> RunDockerAsync(string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("docker")
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start docker");
        var       stdout  = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var       stderr  = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"docker {string.Join(' ', arguments)} exited with {process.ExitCode}: {(await stderr).Trim()}");
        }

        return await stdout + await stderr;
    }

    private static string[] SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}