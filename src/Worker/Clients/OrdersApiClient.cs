namespace Worker.Clients;

// Typed HttpClient for OrdersApi. The standard resilience handler from ServiceDefaults wraps it.
internal sealed class OrdersApiClient
{
    private readonly HttpClient _http;

    public OrdersApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task CreateOrderAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders");
        request.Content = new ByteArrayContent(message.Body.ToArray())
        {
            Headers =
            {
                ContentType = new("application/json")
            },
        };
        request.Headers.Add("x-message-id", message.MessageId);
        request.Headers.Add("x-delivery-count", message.DeliveryCount.ToString());

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}