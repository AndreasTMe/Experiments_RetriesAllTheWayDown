namespace Worker.Messaging;

internal sealed record ProcessorErrorRecord(string ErrorSource, string Reason, string Message, DateTimeOffset At);