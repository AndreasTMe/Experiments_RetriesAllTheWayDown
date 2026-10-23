namespace Runner.Clients;

// A small client for the toxiproxy HTTP API (https://github.com/Shopify/toxiproxy#http-api).
internal sealed class ToxiproxyClient
{
    private const string LatencyToxic   = "latency_downstream";
    private const string TimeoutToxic   = "timeout_downstream";
    private const string ResetPeerToxic = "reset_peer_downstream";

    private readonly HttpClient _http;

    public ToxiproxyClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<string> GetVersionAsync(CancellationToken cancellationToken) =>
        (await _http.GetStringAsync("/version", cancellationToken)).Trim();

    public async Task<JsonNode?> GetProxyAsync(string proxy, CancellationToken cancellationToken) =>
        await _http.GetFromJsonAsync<JsonNode>($"/proxies/{proxy}", cancellationToken);

    public Task EnableProxyAsync(string proxy, CancellationToken cancellationToken) =>
        SetEnabledAsync(proxy, true, cancellationToken);

    public Task DisableProxyAsync(string proxy, CancellationToken cancellationToken) =>
        SetEnabledAsync(proxy, false, cancellationToken);

    // Delays data from Postgres to the client by latencyMs.
    public Task AddLatencyAsync(string proxy, int latencyMs, CancellationToken cancellationToken, int jitterMs = 0) =>
        AddToxicAsync(
            proxy,
            LatencyToxic,
            "latency",
            new JsonObject
            {
                ["latency"] = latencyMs,
                ["jitter"]  = jitterMs
            },
            cancellationToken);

    // Stops all data and closes the connection after timeoutMs (0 = never closes it).
    public Task AddTimeoutAsync(string proxy, int timeoutMs, CancellationToken cancellationToken) =>
        AddToxicAsync(
            proxy,
            TimeoutToxic,
            "timeout",
            new JsonObject
            {
                ["timeout"] = timeoutMs
            },
            cancellationToken);

    // Resets the connection (TCP RST) after timeoutMs.
    public Task AddResetPeerAsync(string proxy, int timeoutMs, CancellationToken cancellationToken) =>
        AddToxicAsync(
            proxy,
            ResetPeerToxic,
            "reset_peer",
            new JsonObject
            {
                ["timeout"] = timeoutMs
            },
            cancellationToken);

    public async Task RemoveToxicAsync(string proxy, string toxic, CancellationToken cancellationToken)
    {
        using var response = await _http.DeleteAsync($"/proxies/{proxy}/toxics/{toxic}", cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    public async Task RemoveAllToxicsAsync(string proxy, CancellationToken cancellationToken)
    {
        foreach (var toxic in new[]
                 {
                     LatencyToxic, TimeoutToxic, ResetPeerToxic
                 })
        {
            await RemoveToxicAsync(proxy, toxic, cancellationToken);
        }
    }

    // Enables every proxy and removes every toxic.
    public async Task ResetAsync(CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsync("/reset", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task SetEnabledAsync(string proxy, bool enabled, CancellationToken cancellationToken)
    {
        using var response = await _http.PostAsJsonAsync(
            $"/proxies/{proxy}",
            new JsonObject
            {
                ["enabled"] = enabled
            },
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task AddToxicAsync(
        string proxy,
        string name,
        string type,
        JsonObject attributes,
        CancellationToken cancellationToken)
    {
        await RemoveToxicAsync(proxy, name, cancellationToken);

        var toxic = new JsonObject
        {
            ["name"]       = name,
            ["type"]       = type,
            ["stream"]     = "downstream",
            ["toxicity"]   = 1.0,
            ["attributes"] = attributes,
        };

        using var response = await _http.PostAsJsonAsync($"/proxies/{proxy}/toxics", toxic, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}