namespace TaskBoard.Worker.Clients;

/// <summary>
/// Provider-agnostic interface for task board operations.
/// Implementations: TrelloClient, GitHubProjectsClient, StubTaskBoardClient.
/// </summary>
public interface ITaskBoardClient
{
    Task<BoardCard> GetCardAsync(string cardId, CancellationToken cancellationToken);
    Task<IReadOnlyList<BoardCard>> GetBoardCardsAsync(string boardId, CancellationToken cancellationToken, IReadOnlyList<string>? excludeStatuses = null);
    Task UpdateCardBodyAsync(string cardId, string body, CancellationToken cancellationToken);
    Task MoveCardToColumnAsync(string cardId, string columnId, CancellationToken cancellationToken);
    Task UpsertAgentCommentAsync(string cardId, string commentBody, string commentMarker, CancellationToken cancellationToken);
    Task<IReadOnlyList<CardComment>> GetCardCommentsAsync(string cardId, CancellationToken cancellationToken);

    // ── Label operations ─────────────────────────────────────────────────────
    Task AddLabelAsync(string cardId, string labelName, CancellationToken cancellationToken);
    Task RemoveLabelAsync(string cardId, string labelName, CancellationToken cancellationToken);

    // ── Assignee operations ──────────────────────────────────────────────────
    Task AssignAsync(string cardId, string username, CancellationToken cancellationToken);
    /// <param name="username">If null, removes all assignees.</param>
    Task UnassignAsync(string cardId, string? username, CancellationToken cancellationToken);

    // ── Custom field operations ──────────────────────────────────────────────
    Task SetFieldAsync(string cardId, string fieldName, string value, CancellationToken cancellationToken);
    Task ClearFieldAsync(string cardId, string fieldName, CancellationToken cancellationToken);

    // ── Card creation ────────────────────────────────────────────────────────
    /// <summary>
    /// Creates a new card (GitHub issue / Trello card) with the given title and body.
    /// Returns the new card's ID (issue number for GitHub, card ID for Trello).
    /// </summary>
    Task<string> CreateCardAsync(string title, string body, CancellationToken cancellationToken);

    // ── Identity ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Returns the username of the currently authenticated agent for this board provider.
    /// Used to resolve {{agent}} template variables in transition actions.
    /// Returns the configured override username if one is set; otherwise resolves from the
    /// authenticated session (e.g. gh CLI user for GitHub).
    /// </summary>
    Task<string> GetCurrentUserAsync(CancellationToken cancellationToken);
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
    IReadOnlyDictionary<string, string>? Metadata = null,
    IReadOnlyList<string>? Labels = null,
    IReadOnlyList<string>? Assignees = null);
