namespace TaskBoard.Worker.Clients;

public sealed record PingMessage(long MessageId, string Source, DateTimeOffset EnqueuedAt);

public interface IPingQueueClient
{
    /// <summary>Read up to batchSize pings with a visibility timeout. Returns empty if none available.</summary>
    Task<IReadOnlyList<PingMessage>> ReadPingsAsync(CancellationToken cancellationToken);

    /// <summary>Archive a processed ping (removes from active queue, keeps in archive).</summary>
    Task ArchivePingAsync(long messageId, CancellationToken cancellationToken);
}
