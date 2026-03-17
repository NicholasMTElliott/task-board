namespace TaskBoard.Worker.Models;

public sealed record QueueMessage(
    long MessageId,
    string ActionId,
    string? CardId,
    string PayloadJson,
    DateTimeOffset EnqueuedAtUtc,
    int ReadCount);