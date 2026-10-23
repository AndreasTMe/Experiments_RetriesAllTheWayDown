namespace Worker.Messaging;

internal sealed record DeliveryRecord(string MessageId, int DeliveryCount, DateTimeOffset ReceivedAt);