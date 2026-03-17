namespace TaskBoard.Worker.Clients;

public interface ITrelloClient
{
    Task<TrelloCard> GetCardAsync(string cardId, CancellationToken cancellationToken);
    Task UpdateCardDescriptionAsync(string cardId, string description, CancellationToken cancellationToken);
    Task MoveCardToListAsync(string cardId, string listId, CancellationToken cancellationToken);
    Task UpsertAgentCommentAsync(string cardId, string commentBody, CancellationToken cancellationToken);
}

public sealed record TrelloCard(string Id, string Name, string Desc, string IdList);
