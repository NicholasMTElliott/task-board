namespace TaskBoard.Worker.Clients;

/// <summary>
/// Request object for creating a new card on the board.
/// Used by both ad-hoc creation and structured generation (generationConfig on steps).
/// Config-specified values take precedence over agent-specified front matter values.
/// </summary>
public sealed record CreateCardRequest(
    string Title,
    string Body,
    string? ParentCardId = null,
    string? CardType = null,
    string? TargetColumn = null,
    IReadOnlyList<string>? Labels = null,
    IReadOnlyDictionary<string, string>? FieldValues = null);

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

    /// <summary>
    /// Posts a NEW comment without checking for existing comments. Used by the
    /// rerun-redesign comment router for chronological log entries (kind:step,
    /// kind:candidate, kind:evaluator, kind:gate, kind:optional, kind:cache_hit)
    /// and as the second leg of delete-and-repost. The caller has already
    /// composed the full body including the <c>aiboard-log</c> marker line.
    /// </summary>
    Task AppendAgentCommentAsync(string cardId, string commentBody, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes every comment on the card whose body contains
    /// <paramref name="markerSubstring"/>. Used by the rerun-redesign comment
    /// router as the first leg of delete-and-repost for status notices
    /// (dependency_blocked, completion_progress, rate_limit_notice,
    /// shutdown_notice, cross_card_notification) so the most current message
    /// stays at the bottom of the comment timeline. No-op when no matching
    /// comments are found.
    /// </summary>
    Task DeleteAgentCommentsByMarkerAsync(string cardId, string markerSubstring, CancellationToken cancellationToken);

    Task<IReadOnlyList<CardComment>> GetCardCommentsAsync(string cardId, CancellationToken cancellationToken);

    // ── Card creation ─────────────────────────────────────────────────────────
    /// <summary>Creates a new card on the board and returns its ID.</summary>
    Task<string> CreateCardAsync(CreateCardRequest request, CancellationToken cancellationToken);

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
