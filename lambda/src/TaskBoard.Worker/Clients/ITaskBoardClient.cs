namespace TaskBoard.Worker.Clients;

/// <summary>
/// Provider-agnostic interface for task board operations.
/// Implementations: TrelloClient, GitHubProjectsClient, StubTaskBoardClient.
/// </summary>
public interface ITaskBoardClient
{
    Task<BoardCard> GetCardAsync(string cardId, CancellationToken cancellationToken);
    Task<IReadOnlyList<BoardCard>> GetBoardCardsAsync(string boardId, CancellationToken cancellationToken);
    Task UpdateCardBodyAsync(string cardId, string body, CancellationToken cancellationToken);
    Task MoveCardToColumnAsync(string cardId, string columnId, CancellationToken cancellationToken);
    Task UpsertAgentCommentAsync(string cardId, string commentBody, CancellationToken cancellationToken);
    Task<IReadOnlyList<CardComment>> GetCardCommentsAsync(string cardId, CancellationToken cancellationToken);
}

public sealed record CardComment(
    string Author,
    string Body,
    DateTimeOffset CreatedAt);

public sealed record BoardCard(
    string Id,
    string Title,
    string Body,
    string ColumnId,
    IReadOnlyDictionary<string, string>? Metadata = null);
