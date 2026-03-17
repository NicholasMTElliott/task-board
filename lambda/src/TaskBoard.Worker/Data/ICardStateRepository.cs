namespace TaskBoard.Worker.Data;

public interface ICardStateRepository
{
    Task<bool> TryAcquireLockAsync(string cardId, string lockId, TimeSpan lockTtl, CancellationToken cancellationToken);
    Task ReleaseLockAsync(string cardId, string lockId, CancellationToken cancellationToken);
    Task<CardState?> GetStateAsync(string cardId, CancellationToken cancellationToken);
    Task UpdateStateAsync(string cardId, string lastProcessedEvent,
        string lastKnownList, string? originListId, bool waitingOnHuman, CancellationToken cancellationToken);
}

public sealed record CardState(string CardId, string? OriginListId, bool WaitingOnHuman);
