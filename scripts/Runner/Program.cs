// Usage:
//   Runner <scenario>             run scenarios/<scenario>.json, e.g. Runner smoke; results go to results/
//   Runner <scenario> --dev       the same, but results go to results/dev/, which git ignores
//   Runner <scenario> --config 2a the same, labelled with the AppHost's configuration: results/<scenario>-cfg2a-*
//   Runner emulator-check         check the emulator's delivery behaviour
//   Runner faults <command>       apply or remove a fault by hand (see FaultsCommand.Usage)
//   Runner list                   list the scenarios

if (args.Length == 0)
{
    Console.WriteLine("Usage: Runner <scenario> [--dev] [--config <label>] | emulator-check | faults <command> | list");
    return 2;
}

// Ctrl+C cancels the command instead of killing the process.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};
var cancellationToken = cancellation.Token;

var             repoRoot = FindRepoRoot();
var             settings = RunnerSettings.Load();
await using var clients  = new ExperimentClients(settings);

try
{
    switch (args[0])
    {
        case "list":
            foreach (var file in Directory.GetFiles(Path.Combine(repoRoot, "scenarios"), "*.json").Order())
            {
                var scenario = Scenario.Load(file);
                Console.WriteLine($"{scenario.Name,-24} {scenario.Description}");
            }

            return 0;

        case "emulator-check":
            return await new EmulatorCheckCommand(clients).RunAsync(cancellationToken);

        case "faults":
            return await FaultsCommand.RunAsync(clients, args[1..], cancellationToken);

        default:
            var path = Path.Combine(repoRoot, "scenarios", $"{args[0]}.json");
            if (!File.Exists(path))
            {
                Console.WriteLine($"No scenario at {path}");
                return 2;
            }

            // --dev for development and check runs: git ignores results/dev/, so they can't mix with the baseline.
            var dev = args[1..].Contains("--dev");
            var resultsRoot = dev
                ? Path.Combine(repoRoot, "results", "dev")
                : Path.Combine(repoRoot, "results");

            // --config labels a run with the configuration the AppHost was started with, so a fix's runs can't be
            // mistaken for the baseline. The switches themselves are read back into config.json either way.
            var configIndex = Array.IndexOf(args, "--config");
            var configuration = configIndex < 0 ? null : args.ElementAtOrDefault(configIndex + 1);
            if (configIndex >= 0 && (configuration is null || !configuration.All(char.IsAsciiLetterOrDigit)))
            {
                Console.WriteLine("--config needs a label of letters and digits, e.g. --config 2a");
                return 2;
            }

            return await new ScenarioCommand(clients, resultsRoot, configuration)
                .RunAsync(Scenario.Load(path), cancellationToken);
    }
}
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
{
    Console.WriteLine("Cancelled");
    return 130;
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (dir.GetFiles("*.slnx").Length > 0)
        {
            return dir.FullName;
        }
    }

    throw new InvalidOperationException(
        "Could not find the repository root (no .slnx above the Runner's output folder)");
}