using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Data;

public interface IQueueRepository
{
    Task<IReadOnlyList<QueueMessage>> ClaimBatchAsync(int maxBatchSize, int visibilityTimeoutSeconds, CancellationToken cancellationToken);
    Task MarkSucceededAsync(long messageId, CancellationToken cancellationToken);
    Task MarkFailedAsync(long messageId, string reason, CancellationToken cancellationToken);
}
