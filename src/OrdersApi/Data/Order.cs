namespace OrdersApi.Data;

internal sealed class Order
{
    public long Id { get; set; }

    public required string MessageId { get; set; }

    public int? DeliveryAttemptHeader { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}