namespace Runner.Clients;

// Talks to the three services' admin endpoints, toxiproxy and the queue.
internal sealed class ExperimentClients : IAsyncDisposable
{
    public RunnerSettings   Settings     { get; }
    public HttpClient       Producer     { get; }
    public HttpClient       Worker       { get; }
    public HttpClient       OrdersApi    { get; }
    public ToxiproxyClient  Toxiproxy    { get; }
    public ToxiproxyLog     ToxiproxyLog { get; }
    public ServiceBusClient ServiceBus   { get; }

    public IEnumerable<(string Name, HttpClient Client)> All =>
        [("producer", Producer), ("worker", Worker), ("ordersapi", OrdersApi)];

    public ExperimentClients(RunnerSettings settings)
    {
        Settings = settings;
        Producer = new HttpClient
        {
            BaseAddress = new Uri(settings.Producer),
            Timeout     = TimeSpan.FromSeconds(30)
        };
        Worker = new HttpClient
        {
            BaseAddress = new Uri(settings.Worker),
            Timeout     = TimeSpan.FromSeconds(30)
        };
        OrdersApi = new HttpClient
        {
            BaseAddress = new Uri(settings.OrdersApi),
            Timeout     = TimeSpan.FromSeconds(30)
        };
        Toxiproxy = new ToxiproxyClient(
            new HttpClient
            {
                BaseAddress = new Uri(settings.Toxiproxy),
                Timeout     = TimeSpan.FromSeconds(30)
            });
        ToxiproxyLog = new ToxiproxyLog(settings.ToxiproxyContainer);
        ServiceBus   = new ServiceBusClient(settings.ServiceBus);
    }

    public Task SetApiFaultsAsync(
        double errorRate,
        int capacity,
        int serviceTimeMs,
        CancellationToken cancellationToken) =>
        PutAsync(
            OrdersApi,
            "/admin/faults",
            new
            {
                errorRate,
                capacity,
                serviceTimeMs
            },
            cancellationToken);

    public Task ClearApiFaultsAsync(CancellationToken cancellationToken) =>
        SetApiFaultsAsync(0, 0, 0, cancellationToken);

    // Null puts the Worker back on the switches it started with. Either way it restarts its processor.
    public async Task SetWorkerSwitchesAsync(JsonObject? switches, CancellationToken cancellationToken)
    {
        if (switches is not null)
        {
            await PutAsync(Worker, "/admin/experiment", switches, cancellationToken);
            return;
        }

        using var response = await Worker.DeleteAsync("/admin/experiment", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    // Receives and deletes everything in the queue and its dead-letter queue.
    public async Task<(int Active, int DeadLettered)> PurgeQueueAsync(CancellationToken cancellationToken)
    {
        var active = await ReceiveAndDeleteAllAsync(
            new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete
            },
            cancellationToken);
        var deadLettered = await ReceiveAndDeleteAllAsync(
            new ServiceBusReceiverOptions
            {
                ReceiveMode = ServiceBusReceiveMode.ReceiveAndDelete,
                SubQueue    = SubQueue.DeadLetter,
            },
            cancellationToken);
        return (active, deadLettered);
    }

    // Peeks the dead-letter queue (does not remove anything).
    public async Task<List<ServiceBusReceivedMessage>> PeekDeadLettersAsync(CancellationToken cancellationToken)
    {
        await using var receiver = ServiceBus.CreateReceiver(
            Settings.Queue,
            new ServiceBusReceiverOptions
            {
                SubQueue = SubQueue.DeadLetter
            });
        var   all                = new List<ServiceBusReceivedMessage>();
        long? fromSequenceNumber = null;
        while (true)
        {
            var batch = await receiver.PeekMessagesAsync(250, fromSequenceNumber, cancellationToken);
            if (batch.Count == 0)
            {
                return all;
            }

            all.AddRange(batch);
            fromSequenceNumber = batch[^1].SequenceNumber + 1;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await ServiceBus.DisposeAsync();
        Producer.Dispose();
        Worker.Dispose();
        OrdersApi.Dispose();
    }

    private async Task<int> ReceiveAndDeleteAllAsync(
        ServiceBusReceiverOptions options,
        CancellationToken cancellationToken)
    {
        await using var receiver = ServiceBus.CreateReceiver(Settings.Queue, options);
        var             total    = 0;
        while (true)
        {
            var batch = await receiver.ReceiveMessagesAsync(100, TimeSpan.FromSeconds(2), cancellationToken);
            if (batch.Count == 0)
            {
                return total;
            }

            total += batch.Count;
        }
    }

    public static async Task PostAsync(
        HttpClient client,
        string path,
        CancellationToken cancellationToken,
        object? body = null)
    {
        using var response = body is null
            ? await client.PostAsync(path, null, cancellationToken)
            : await client.PostAsJsonAsync(path, body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public static async Task PutAsync(HttpClient client, string path, object body, CancellationToken cancellationToken)
    {
        using var response = await client.PutAsJsonAsync(path, body, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public static async Task<JsonNode>
        GetJsonAsync(HttpClient client, string path, CancellationToken cancellationToken) =>
        await client.GetFromJsonAsync<JsonNode>(path, cancellationToken)
        ?? throw new InvalidOperationException($"Empty response from {path}");
}