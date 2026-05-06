namespace TaskBoard.Worker.Clients;

public sealed record CardDependency(
    string CardId,
    string? Title = null,
    string? ColumnId = null,
    bool? IsClosed = null,
    string? StateReason = null);

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
