namespace TaskBoard.Worker.Clients;

public sealed class StubTaskBoardClient(ILogger<StubTaskBoardClient> logger) : ITaskBoardClient
{
    public Task<BoardCard> GetCardAsync(string cardId, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] GetCard {CardId}", cardId);
        return Task.FromResult(new BoardCard(
            cardId,
            "Stub Card",
            "# Requirements\nStub requirements content",
            "stub-list-id"));
    }

    public Task<IReadOnlyList<BoardCard>> GetBoardCardsAsync(string boardId, CancellationToken cancellationToken, IReadOnlyList<string>? excludeStatuses = null)
    {
        logger.LogInformation("[Stub] GetBoardCards {BoardId}", boardId);
        IReadOnlyList<BoardCard> cards = new List<BoardCard>
        {
            new("stub-card-1", "Stub Card 1", "# Requirements\nStub requirements", "stub-list-id"),
            new("stub-card-2", "Stub Card 2", "# Design\nStub design", "stub-list-id"),
        };
        return Task.FromResult(cards);
    }

    public Task UpdateCardBodyAsync(string cardId, string body, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] UpdateCardBody {CardId} ({Length} chars)", cardId, body.Length);
        return Task.CompletedTask;
    }

    public Task MoveCardToColumnAsync(string cardId, string columnId, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] MoveCard {CardId} → {ColumnId}", cardId, columnId);
        return Task.CompletedTask;
    }

    public Task UpsertAgentCommentAsync(string cardId, string commentBody, string commentMarker, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] UpsertComment {CardId} ({Length} chars)", cardId, commentBody.Length);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<CardComment>> GetCardCommentsAsync(string cardId, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] GetCardComments {CardId}", cardId);
        return Task.FromResult<IReadOnlyList<CardComment>>(new List<CardComment>());
    }

    public Task AddLabelAsync(string cardId, string labelName, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] AddLabel {CardId} label={Label}", cardId, labelName);
        return Task.CompletedTask;
    }

    public Task RemoveLabelAsync(string cardId, string labelName, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] RemoveLabel {CardId} label={Label}", cardId, labelName);
        return Task.CompletedTask;
    }

    public Task AssignAsync(string cardId, string username, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] Assign {CardId} user={User}", cardId, username);
        return Task.CompletedTask;
    }

    public Task UnassignAsync(string cardId, string? username, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] Unassign {CardId} user={User}", cardId, username ?? "(all)");
        return Task.CompletedTask;
    }

    public Task SetFieldAsync(string cardId, string fieldName, string value, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] SetField {CardId} field={Field} value={Value}", cardId, fieldName, value);
        return Task.CompletedTask;
    }

    public Task ClearFieldAsync(string cardId, string fieldName, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] ClearField {CardId} field={Field}", cardId, fieldName);
        return Task.CompletedTask;
    }

    private int _nextStubId;

    public Task<string> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] GetCurrentUser");
        return Task.FromResult("stub-agent");
    }

    public Task<string> CreateCardAsync(CreateCardRequest request, CancellationToken cancellationToken)
    {
        var id = System.Threading.Interlocked.Increment(ref _nextStubId).ToString();
        logger.LogInformation(
            "[Stub] CreateCard #{Id} title={Title} type={Type} parent={Parent} column={Column}",
            id, request.Title, request.CardType ?? "(none)",
            request.ParentCardId ?? "(none)", request.TargetColumn ?? "(none)");
        return Task.FromResult(id);
    }
}
