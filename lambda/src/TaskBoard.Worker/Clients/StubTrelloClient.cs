namespace TaskBoard.Worker.Clients;

public sealed class StubTrelloClient(ILogger<StubTrelloClient> logger) : ITrelloClient
{
    public Task<TrelloCard> GetCardAsync(string cardId, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] GetCard {CardId}", cardId);
        return Task.FromResult(new TrelloCard(
            cardId,
            "Stub Card",
            "# Requirements\nStub requirements content",
            "stub-list-id"));
    }

    public Task<IReadOnlyList<TrelloCard>> GetBoardCardsAsync(string boardId, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] GetBoardCards {BoardId}", boardId);
        IReadOnlyList<TrelloCard> cards = new List<TrelloCard>
        {
            new("stub-card-1", "Stub Card 1", "# Requirements\nStub requirements", "stub-list-id"),
            new("stub-card-2", "Stub Card 2", "# Design\nStub design", "stub-list-id"),
        };
        return Task.FromResult(cards);
    }

    public Task UpdateCardDescriptionAsync(string cardId, string description, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] UpdateCardDescription {CardId} ({Length} chars)", cardId, description.Length);
        return Task.CompletedTask;
    }

    public Task MoveCardToListAsync(string cardId, string listId, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] MoveCard {CardId} → {ListId}", cardId, listId);
        return Task.CompletedTask;
    }

    public Task UpsertAgentCommentAsync(string cardId, string commentBody, CancellationToken cancellationToken)
    {
        logger.LogInformation("[Stub] UpsertComment {CardId} ({Length} chars)", cardId, commentBody.Length);
        return Task.CompletedTask;
    }
}
