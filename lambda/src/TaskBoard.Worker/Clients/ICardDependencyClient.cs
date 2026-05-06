namespace TaskBoard.Worker.Clients;

public sealed record CardDependency(
    string CardId,
    string? Title = null,
    string? ColumnId = null,
    bool? IsClosed = null,
    string? StateReason = null);

/// <summary>
/// Thrown when the dependency provider's response shape diverges from the
/// expected contract (e.g. a list endpoint returned a non-array root).
/// Treated as fatal by <c>DependencyGuard</c> — propagated to the caller so
/// the operator notices and can fix the upstream contract change. Distinct
/// from transient lookup failures (auth, 5xx, network), which are caught and
/// treated as "not blocked".
/// </summary>
public sealed class DependencyApiContractException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

public interface ICardDependencyClient
{
    Task<IReadOnlyList<CardDependency>> GetBlockersAsync(string cardId, CancellationToken cancellationToken);
    Task<IReadOnlyList<CardDependency>> GetBlockedCardsAsync(string cardId, CancellationToken cancellationToken);
    Task AddBlockedByAsync(string blockedCardId, string blockerCardId, CancellationToken cancellationToken);
    Task RemoveBlockedByAsync(string blockedCardId, string blockerCardId, CancellationToken cancellationToken);
}

public sealed class NullCardDependencyClient : ICardDependencyClient
{
    public static readonly NullCardDependencyClient Instance = new();

    private NullCardDependencyClient() { }

    public Task<IReadOnlyList<CardDependency>> GetBlockersAsync(string cardId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CardDependency>>([]);

    public Task<IReadOnlyList<CardDependency>> GetBlockedCardsAsync(string cardId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<CardDependency>>([]);

    public Task AddBlockedByAsync(string blockedCardId, string blockerCardId, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public Task RemoveBlockedByAsync(string blockedCardId, string blockerCardId, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
