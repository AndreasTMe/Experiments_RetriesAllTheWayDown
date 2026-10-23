namespace Worker.Messaging;

internal sealed class QueueCounts
{
    public long Active { get; set; } = -1;

    public long DeadLettered { get; set; } = -1;
}