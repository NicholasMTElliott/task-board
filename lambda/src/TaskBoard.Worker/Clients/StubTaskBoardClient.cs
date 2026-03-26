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

    public Task<IReadOnlyList<BoardCard>> GetBoardCardsAsync(string boardId, CancellationToken cancellationToken)
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
}
