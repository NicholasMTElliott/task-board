using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TaskBoard.Worker.Clients;

public sealed class TrelloClient(
    HttpClient httpClient,
    IOptions<TrelloClientOptions> options,
    ILogger<TrelloClient> logger) : ITaskBoardClient
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient = httpClient;
    private readonly TrelloClientOptions _options = options.Value;
    private readonly ILogger<TrelloClient> _logger = logger;

    private sealed record TrelloCard(
        string Id,
        string Name,
        string Desc,
        string IdList,
        List<TrelloLabel>? Labels = null,
        List<TrelloMember>? Members = null);

    private sealed record TrelloLabel(string Id, string Name);
    private sealed record TrelloMember(string Id, string Username);

    public async Task<BoardCard> GetCardAsync(string cardId, CancellationToken cancellationToken)
    {
        var url = ($"/1/cards/{cardId}?fields=id,name,desc,idList&labels=true&members=true&member_fields=username");
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        await EnsureSuccessOrThrow(response, "GetCard", cardId);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var card = JsonSerializer.Deserialize<TrelloCard>(json, JsonOptions)
               ?? throw new TrelloApiException("GetCard", cardId, response.StatusCode, "Null deserialization result");
        return ToBoardCard(card);
    }

    public async Task<IReadOnlyList<BoardCard>> GetBoardCardsAsync(string boardId, CancellationToken cancellationToken, IReadOnlyList<string>? excludeStatuses = null)
    {
        var url = ($"/1/boards/{boardId}/cards?fields=id,name,desc,idList&labels=true&members=true&member_fields=username");
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        await EnsureSuccessOrThrow(response, "GetBoardCards", boardId);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var cards = JsonSerializer.Deserialize<List<TrelloCard>>(json, JsonOptions)
               ?? throw new TrelloApiException("GetBoardCards", boardId, response.StatusCode, "Null deserialization result");
        return cards.Select(ToBoardCard).ToList();
    }

    public async Task UpdateCardBodyAsync(string cardId, string body, CancellationToken cancellationToken)
    {
        var url = ($"/1/cards/{cardId}");
        using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("desc", body) });
        using var response = await _httpClient.PutAsync(url, content, cancellationToken);
        await EnsureSuccessOrThrow(response, "UpdateCardDescription", cardId);

        _logger.LogInformation("Updated description for card {CardId} ({Length} chars)", cardId, body.Length);
    }

    public async Task MoveCardToColumnAsync(string cardId, string columnId, CancellationToken cancellationToken)
    {
        var url = ($"/1/cards/{cardId}/idList");
        using var content = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("value", columnId) });
        using var response = await _httpClient.PutAsync(url, content, cancellationToken);
        await EnsureSuccessOrThrow(response, "MoveCardToList", cardId);

        _logger.LogInformation("Moved card {CardId} to list {ListId}", cardId, columnId);
    }

    public async Task UpsertAgentCommentAsync(string cardId, string commentBody, string commentMarker, CancellationToken cancellationToken)
    {
        var markedBody = $"{commentMarker}\n{commentBody}";

        // Search for existing agent comment
        var searchUrl = ($"/1/cards/{cardId}/actions?filter=commentCard");
        using var searchResponse = await _httpClient.GetAsync(searchUrl, cancellationToken);
        await EnsureSuccessOrThrow(searchResponse, "SearchComments", cardId);

        var searchJson = await searchResponse.Content.ReadAsStringAsync(cancellationToken);
        var existingCommentId = FindAgentCommentId(searchJson, commentMarker);

        if (existingCommentId is not null)
        {
            // Update existing comment
            var updateUrl = ($"/1/actions/{existingCommentId}/text");
            using var updateContent = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("value", markedBody) });
            using var updateResponse = await _httpClient.PutAsync(updateUrl, updateContent, cancellationToken);
            await EnsureSuccessOrThrow(updateResponse, "UpdateComment", cardId);

            _logger.LogInformation("Updated agent comment {CommentId} on card {CardId}", existingCommentId, cardId);
        }
        else
        {
            // Create new comment
            var createUrl = ($"/1/cards/{cardId}/actions/comments");
            using var createContent = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("text", markedBody) });
            using var createResponse = await _httpClient.PostAsync(createUrl, createContent, cancellationToken);
            await EnsureSuccessOrThrow(createResponse, "CreateComment", cardId);

            _logger.LogInformation("Created new agent comment on card {CardId}", cardId);
        }
    }

    public async Task<IReadOnlyList<CardComment>> GetCardCommentsAsync(string cardId, CancellationToken cancellationToken)
    {
        var url = ($"/1/cards/{cardId}/actions?filter=commentCard&limit=1000");
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        await EnsureSuccessOrThrow(response, "GetCardComments", cardId);

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        var comments = new List<CardComment>();
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var author = item.TryGetProperty("memberCreator", out var mc)
                ? (mc.TryGetProperty("fullName", out var fn) ? fn.GetString() : null)
                  ?? (mc.TryGetProperty("username", out var un) ? un.GetString() : null)
                  ?? "unknown"
                : "unknown";
            var body = item.TryGetProperty("data", out var data)
                && data.TryGetProperty("text", out var text)
                ? text.GetString() ?? ""
                : "";
            var createdAt = item.TryGetProperty("date", out var dateProp)
                ? DateTimeOffset.Parse(dateProp.GetString()!)
                : DateTimeOffset.MinValue;

            comments.Add(new CardComment(author, body, createdAt));
        }

        comments.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        _logger.LogInformation("Fetched {Count} comments for card {CardId}", comments.Count, cardId);
        return comments;
    }

    public async Task AddLabelAsync(string cardId, string labelName, CancellationToken cancellationToken)
    {
        // Resolve label ID from name on the board
        var labelId = await ResolveLabelIdAsync(cardId, labelName, cancellationToken);
        var url = ($"/1/cards/{cardId}/idLabels");
        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("value", labelId)]);
        using var response = await _httpClient.PostAsync(url, content, cancellationToken);
        await EnsureSuccessOrThrow(response, "AddLabel", cardId);
        _logger.LogInformation("Added label '{Label}' to card {CardId}", labelName, cardId);
    }

    public async Task RemoveLabelAsync(string cardId, string labelName, CancellationToken cancellationToken)
    {
        var labelId = await ResolveLabelIdAsync(cardId, labelName, cancellationToken);
        var url = ($"/1/cards/{cardId}/idLabels/{labelId}");
        using var response = await _httpClient.DeleteAsync(url, cancellationToken);
        await EnsureSuccessOrThrow(response, "RemoveLabel", cardId);
        _logger.LogInformation("Removed label '{Label}' from card {CardId}", labelName, cardId);
    }

    public async Task AssignAsync(string cardId, string username, CancellationToken cancellationToken)
    {
        var memberId = await ResolveMemberIdAsync(cardId, username, cancellationToken);
        var url = ($"/1/cards/{cardId}/idMembers");
        using var content = new FormUrlEncodedContent([new KeyValuePair<string, string>("value", memberId)]);
        using var response = await _httpClient.PostAsync(url, content, cancellationToken);
        await EnsureSuccessOrThrow(response, "Assign", cardId);
        _logger.LogInformation("Assigned '{User}' to card {CardId}", username, cardId);
    }

    public async Task UnassignAsync(string cardId, string? username, CancellationToken cancellationToken)
    {
        if (username is not null)
        {
            var memberId = await ResolveMemberIdAsync(cardId, username, cancellationToken);
            var url = ($"/1/cards/{cardId}/idMembers/{memberId}");
            using var response = await _httpClient.DeleteAsync(url, cancellationToken);
            await EnsureSuccessOrThrow(response, "Unassign", cardId);
            _logger.LogInformation("Unassigned '{User}' from card {CardId}", username, cardId);
        }
        else
        {
            // Remove all members
            var url = ($"/1/cards/{cardId}?fields=idMembers");
            using var getResponse = await _httpClient.GetAsync(url, cancellationToken);
            await EnsureSuccessOrThrow(getResponse, "GetCardMembers", cardId);
            var json = await getResponse.Content.ReadAsStringAsync(cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("idMembers", out var memberIds))
            {
                foreach (var mid in memberIds.EnumerateArray())
                {
                    var mId = mid.GetString();
                    if (mId is null) continue;
                    var removeUrl = ($"/1/cards/{cardId}/idMembers/{mId}");
                    using var removeResponse = await _httpClient.DeleteAsync(removeUrl, cancellationToken);
                    // Best effort per member
                }
            }
            _logger.LogInformation("Removed all members from card {CardId}", cardId);
        }
    }

    public Task SetFieldAsync(string cardId, string fieldName, string value, CancellationToken cancellationToken)
    {
        // NOTE: Trello custom field operations require the Custom Fields Power-Up
        // and field-ID resolution which is not yet implemented.
        // Track as follow-up when working on the Trello provider next.
        throw new NotSupportedException(
            "Trello custom field operations require the Custom Fields Power-Up. Not yet implemented. " +
            "Track as follow-up when working on the Trello provider.");
    }

    public Task ClearFieldAsync(string cardId, string fieldName, CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "Trello custom field operations require the Custom Fields Power-Up. Not yet implemented. " +
            "Track as follow-up when working on the Trello provider.");
    }

    public Task<string> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.AgentUsername))
            return Task.FromResult(_options.AgentUsername);

        // Without a configured username, return empty string; assign actions will fail gracefully
        // via the lenient error handling in TransitionExecutor.
        _logger.LogWarning("Trello AgentUsername is not configured; {{agent}} template will resolve to empty string");
        return Task.FromResult(string.Empty);
    }

    private async Task<string> ResolveLabelIdAsync(string cardId, string labelName, CancellationToken cancellationToken)
    {
        // Get labels from the card to find the board ID, then look up the label
        var cardUrl = ($"/1/cards/{cardId}?fields=idBoard");
        using var cardResp = await _httpClient.GetAsync(cardUrl, cancellationToken);
        await EnsureSuccessOrThrow(cardResp, "GetCardBoard", cardId);
        var cardJson = await cardResp.Content.ReadAsStringAsync(cancellationToken);
        using var cardDoc = JsonDocument.Parse(cardJson);
        var boardId = cardDoc.RootElement.GetProperty("idBoard").GetString()
            ?? throw new InvalidOperationException($"Could not get board ID for card {cardId}");

        var labelsUrl = ($"/1/boards/{boardId}/labels?limit=1000");
        using var labelsResp = await _httpClient.GetAsync(labelsUrl, cancellationToken);
        await EnsureSuccessOrThrow(labelsResp, "GetBoardLabels", boardId);
        var labelsJson = await labelsResp.Content.ReadAsStringAsync(cancellationToken);
        using var labelsDoc = JsonDocument.Parse(labelsJson);

        foreach (var label in labelsDoc.RootElement.EnumerateArray())
        {
            if (label.TryGetProperty("name", out var name)
                && string.Equals(name.GetString(), labelName, StringComparison.OrdinalIgnoreCase)
                && label.TryGetProperty("id", out var id))
            {
                return id.GetString() ?? throw new InvalidOperationException($"Null label ID for '{labelName}'");
            }
        }

        throw new InvalidOperationException($"Label '{labelName}' not found on Trello board {boardId}");
    }

    private async Task<string> ResolveMemberIdAsync(string cardId, string username, CancellationToken cancellationToken)
    {
        var cardUrl = ($"/1/cards/{cardId}?fields=idBoard");
        using var cardResp = await _httpClient.GetAsync(cardUrl, cancellationToken);
        await EnsureSuccessOrThrow(cardResp, "GetCardBoard", cardId);
        var cardJson = await cardResp.Content.ReadAsStringAsync(cancellationToken);
        using var cardDoc = JsonDocument.Parse(cardJson);
        var boardId = cardDoc.RootElement.GetProperty("idBoard").GetString()
            ?? throw new InvalidOperationException($"Could not get board ID for card {cardId}");

        var membersUrl = ($"/1/boards/{boardId}/members");
        using var membersResp = await _httpClient.GetAsync(membersUrl, cancellationToken);
        await EnsureSuccessOrThrow(membersResp, "GetBoardMembers", boardId);
        var membersJson = await membersResp.Content.ReadAsStringAsync(cancellationToken);
        using var membersDoc = JsonDocument.Parse(membersJson);

        foreach (var member in membersDoc.RootElement.EnumerateArray())
        {
            if (member.TryGetProperty("username", out var uname)
                && string.Equals(uname.GetString(), username, StringComparison.OrdinalIgnoreCase)
                && member.TryGetProperty("id", out var id))
            {
                return id.GetString() ?? throw new InvalidOperationException($"Null member ID for '{username}'");
            }
        }

        throw new InvalidOperationException($"Member '{username}' not found on Trello board {boardId}");
    }

    private static BoardCard ToBoardCard(TrelloCard card)
    {
        var labels = card.Labels?.Select(l => l.Name).Where(n => n is not null).ToList();
        var assignees = card.Members?.Select(m => m.Username).Where(u => u is not null).ToList();
        return new BoardCard(card.Id, card.Name, card.Desc, card.IdList,
            Labels: labels, Assignees: assignees);
    }

    private string? FindAgentCommentId(string actionsJson, string commentMarker)
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
                    if (textValue is not null && textValue.Contains(commentMarker, StringComparison.Ordinal))
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

    /// <summary>
    /// Strips Trello API credentials (key/token query params) from a URL for safe logging/error messages.
    /// </summary>
    internal static string SanitizeUrl(string url)
    {
        return System.Text.RegularExpressions.Regex.Replace(
            url, @"([?&])(key|token)=[^&]*", m => m.Groups[1].Value == "?" ? "?" : "")
            .Replace("?&", "?").TrimEnd('?');
    }

    /// <summary>
    /// Delegating handler that appends Trello API key and token to all outbound requests.
    /// Keeps credentials out of URL strings constructed in client code, so exceptions and
    /// logs from <see cref="HttpClient"/> never expose them.
    /// </summary>
    internal sealed class TrelloAuthHandler(TrelloClientOptions options) : DelegatingHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var separator = string.IsNullOrEmpty(uri.Query) ? "?" : "&";
            request.RequestUri = new Uri(
                $"{uri.GetLeftPart(UriPartial.Path)}{uri.Query}{separator}key={options.ApiKey}&token={options.ApiToken}",
                UriKind.Absolute);
            return base.SendAsync(request, cancellationToken);
        }
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
