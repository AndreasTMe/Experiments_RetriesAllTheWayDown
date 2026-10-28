namespace Runner.Commands;

// Counts per message at each layer, from the services' /admin/stats/messages and the rows per message in Postgres.
// A message the fault hit is one whose count at any layer differs from the clean path: 1 delivery, 1 HTTP attempt,
// 1 request, 1 connection open, 1 command, 0 EF retries, 1 row. The run averages in attemptsPerMessage divide by every
// message sent, so they hide what happened to these. Postgres's own INSERT count (pg_stat_statements) is not kept per
// message, so it is not here.
internal sealed class PerMessageCounts
{
    private const string Definition = "Messages whose count at any layer differs from the clean path: 1 delivery, "
                                      + "1 HTTP attempt, 1 request, 1 connection open, 1 command, 0 EF retries, 1 row";

    private static readonly (string Name, long Clean)[] Layers =
    [
        ("serviceBusDeliveries", 1),
        ("httpAttemptsFromWorker", 1),
        ("httpRequestsAtApi", 1),
        ("dbConnectionOpens", 1),
        ("dbCommands", 1),
        ("efRetries", 0),
        ("rows", 1),
    ];

    // Reported next to the layers, but not part of the clean-path test.
    private static readonly string[] Extras = ["abandoned", "rescheduled", "callerGoneWhileRunning"];

    private readonly Dictionary<string, Dictionary<string, long>> _messages;

    private static IEnumerable<string> Columns => Layers.Select(l => l.Name).Concat(Extras);

    private PerMessageCounts(Dictionary<string, Dictionary<string, long>> messages)
    {
        _messages = messages;
    }

    public JsonObject Summarize()
    {
        var hit = _messages.Values.Where(IsHit).ToList();

        var perLayer = new JsonObject();
        foreach (var column in Columns)
        {
            var values = hit.Select(m => m[column]).ToList();
            perLayer[column] = new JsonObject
            {
                ["total"]      = values.Sum(),
                ["perMessage"] = values.Count == 0 ? null : Math.Round(values.Average(), 3),
                ["max"]        = values.Count == 0 ? null : values.Max(),
            };
        }

        return new JsonObject
        {
            ["definition"]      = Definition,
            ["messagesCounted"] = _messages.Count,
            ["cleanMessages"]   = _messages.Count - hit.Count,
            ["hitMessages"]     = hit.Count,
            ["perLayer"]        = perLayer,
        };
    }

    // One row per message, the ones the fault hit first, most database commands first.
    public string ToCsv()
    {
        var csv = new StringBuilder();
        csv.AppendLine(
            string.Join(
                ',',
                new[]
                {
                    "messageId", "hit"
                }.Concat(Columns)));
        foreach (var (id, counts) in _messages
                     .OrderByDescending(m => IsHit(m.Value))
                     .ThenByDescending(m => m.Value["dbCommands"])
                     .ThenBy(m => m.Key, StringComparer.Ordinal))
        {
            csv.AppendLine(
                string.Join(
                    ',',
                    new[]
                    {
                        id, IsHit(counts) ? "1" : "0"
                    }.Concat(Columns.Select(c => counts[c].ToString()))));
        }

        return csv.ToString();
    }

    public static async Task<PerMessageCounts> CollectAsync(
        ExperimentClients clients,
        CancellationToken cancellationToken)
    {
        var worker = (await ExperimentClients.GetJsonAsync(clients.Worker, "/admin/stats/messages", cancellationToken))
            .AsObject();
        var api = (await ExperimentClients.GetJsonAsync(clients.OrdersApi, "/admin/stats/messages", cancellationToken))
            .AsObject();
        var rows = (await ExperimentClients.GetJsonAsync(
                clients.OrdersApi,
                "/admin/groundtruth/messages",
                cancellationToken))
            .AsObject();

        static long Get(JsonObject source, string messageId, string counter) =>
            source[messageId]?[counter]?.GetValue<long>() ?? 0;

        var messages = worker.Select(m => m.Key)
            .Concat(api.Select(m => m.Key))
            .Concat(rows.Select(m => m.Key))
            .Distinct()
            .ToDictionary(
                id => id,
                id => new Dictionary<string, long>
                {
                    ["serviceBusDeliveries"]   = Get(worker, id, "received"),
                    ["httpAttemptsFromWorker"] = Get(worker, id, "http_attempts"),
                    ["httpRequestsAtApi"]      = Get(api, id, "orders_requests"),
                    ["dbConnectionOpens"] = Get(api, id, "db_connection_opened")
                                            + Get(api, id, "db_connection_failed"),
                    ["dbCommands"] = Get(api, id, "db_command_executed")
                                     + Get(api, id, "db_command_failed")
                                     + Get(api, id, "db_command_canceled"),
                    ["efRetries"]              = Get(api, id, "ef_retries"),
                    ["rows"]                   = rows[id]?.GetValue<long>() ?? 0,
                    ["abandoned"]              = Get(worker, id, "abandoned"),
                    ["rescheduled"]            = Get(worker, id, "rescheduled"),
                    ["callerGoneWhileRunning"] = Get(api, id, "orders_caller_gone_while_running"),
                });

        return new PerMessageCounts(messages);
    }

    private static bool IsHit(Dictionary<string, long> counts) =>
        Layers.Any(layer => counts[layer.Name] != layer.Clean);
}