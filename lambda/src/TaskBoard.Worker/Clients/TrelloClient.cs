using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TaskBoard.Worker.Clients;

public sealed class TrelloClient(
    HttpClient httpClient,
    IOptions<TrelloClientOptions> options,
    ILogger<TrelloClient> logger) : ITrelloClient, ITaskBoardClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient = httpClient;
    private readonly TrelloClientOptions _options = options.Value;
    private readonly ILogger<TrelloClient> _logger = logger;

    public async Task<TrelloCard> GetCardAsync(string cardId, CancellationToken cancellationToken)
    {
        var url = AppendAuth($"/1/cards/{cardId}?fields=id,name,desc,idList");
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        await EnsureSuccessOrThrow(response, "GetCard", cardId);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<TrelloCard>(json, JsonOptions)
               ?? throw new TrelloApiException("GetCard", cardId, response.StatusCode, "Null deserialization result");
    }

    public async Task<IReadOnlyList<TrelloCard>> GetBoardCardsAsync(string boardId, CancellationToken cancellationToken)
    {
        var url = AppendAuth($"/1/boards/{boardId}/cards?fields=id,name,desc,idList");
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        await EnsureSuccessOrThrow(response, "GetBoardCards", boardId);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<TrelloCard>>(json, JsonOptions)
               ?? throw new TrelloApiException("GetBoardCards", boardId, response.StatusCode, "Null deserialization result");
    }

    public async Task UpdateCardDescriptionAsync(string cardId, string description, CancellationToken cancellationToken)
    {
        var url = AppendAuth($"/1/cards/{cardId}");
        using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("desc", description) });
        using var response = await _httpClient.PutAsync(url, content, cancellationToken);
        await EnsureSuccessOrThrow(response, "UpdateCardDescription", cardId);

        _logger.LogInformation("Updated description for card {CardId} ({Length} chars)", cardId, description.Length);
    }

    public async Task MoveCardToListAsync(string cardId, string listId, CancellationToken cancellationToken)
    {
        var url = AppendAuth($"/1/cards/{cardId}/idList");
        using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("value", listId) });
        using var response = await _httpClient.PutAsync(url, content, cancellationToken);
        await EnsureSuccessOrThrow(response, "MoveCardToList", cardId);

        _logger.LogInformation("Moved card {CardId} to list {ListId}", cardId, listId);
    }

    public async Task UpsertAgentCommentAsync(string cardId, string commentBody, CancellationToken cancellationToken)
    {
        var markedBody = $"{_options.AgentCommentMarker}\n{commentBody}";

        // Search for existing agent comment
        var searchUrl = AppendAuth($"/1/cards/{cardId}/actions?filter=commentCard");
        using var searchResponse = await _httpClient.GetAsync(searchUrl, cancellationToken);
        await EnsureSuccessOrThrow(searchResponse, "SearchComments", cardId);

        var searchJson = await searchResponse.Content.ReadAsStringAsync(cancellationToken);
        var existingCommentId = FindAgentCommentId(searchJson);

        if (existingCommentId is not null)
        {
            // Update existing comment
            var updateUrl = AppendAuth($"/1/actions/{existingCommentId}/text");
            using var updateContent = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("value", markedBody) });
            using var updateResponse = await _httpClient.PutAsync(updateUrl, updateContent, cancellationToken);
            await EnsureSuccessOrThrow(updateResponse, "UpdateComment", cardId);

            _logger.LogInformation("Updated agent comment {CommentId} on card {CardId}", existingCommentId, cardId);
        }
        else
        {
            // Create new comment
            var createUrl = AppendAuth($"/1/cards/{cardId}/actions/comments");
            using var createContent = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("text", markedBody) });
            using var createResponse = await _httpClient.PostAsync(createUrl, createContent, cancellationToken);
            await EnsureSuccessOrThrow(createResponse, "CreateComment", cardId);

            _logger.LogInformation("Created new agent comment on card {CardId}", cardId);
        }
    }

    // ITaskBoardClient explicit implementations — delegate to Trello-specific methods

    async Task<BoardCard> ITaskBoardClient.GetCardAsync(string cardId, CancellationToken cancellationToken)
    {
        var card = await GetCardAsync(cardId, cancellationToken);
        return ToBoardCard(card);
    }

    async Task<IReadOnlyList<BoardCard>> ITaskBoardClient.GetBoardCardsAsync(string boardId, CancellationToken cancellationToken)
    {
        var cards = await GetBoardCardsAsync(boardId, cancellationToken);
        return cards.Select(ToBoardCard).ToList();
    }

    Task ITaskBoardClient.UpdateCardBodyAsync(string cardId, string body, CancellationToken cancellationToken)
        => UpdateCardDescriptionAsync(cardId, body, cancellationToken);

    Task ITaskBoardClient.MoveCardToColumnAsync(string cardId, string columnId, CancellationToken cancellationToken)
        => MoveCardToListAsync(cardId, columnId, cancellationToken);

    Task ITaskBoardClient.UpsertAgentCommentAsync(string cardId, string commentBody, CancellationToken cancellationToken)
        => UpsertAgentCommentAsync(cardId, commentBody, cancellationToken);

    private static BoardCard ToBoardCard(TrelloCard card)
        => new(card.Id, card.Name, card.Desc, card.IdList);

    private string? FindAgentCommentId(string actionsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(actionsJson);
            foreach (var action in doc.RootElement.EnumerateArray())
            {
                if (action.TryGetProperty("data", out var data)
                    && data.TryGetProperty("text", out var text))
                {
                    var textValue = text.GetString();
                    if (textValue is not null && textValue.Contains(_options.AgentCommentMarker, StringComparison.Ordinal))
                    {
                        if (action.TryGetProperty("id", out var id))
                        {
                            return id.GetString();
                        }
                    }
                }
            }
        }
        catch (JsonException)
        {
            _logger.LogWarning("Failed to parse comment actions JSON");
        }

        return null;
    }

    private string AppendAuth(string url)
    {
        var separator = url.Contains('?') ? '&' : '?';
        return $"{url}{separator}key={_options.ApiKey}&token={_options.ApiToken}";
    }

    private static async Task EnsureSuccessOrThrow(HttpResponseMessage response, string operation, string resourceId)
    {
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new TrelloApiException(operation, resourceId, response.StatusCode, body);
        }
    }
}
