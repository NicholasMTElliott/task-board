namespace TaskBoard.Worker.Clients;

public sealed class TrelloClientOptions
{
    public const string SectionName = "Trello";

    public string ApiKey { get; init; } = string.Empty;

    public string ApiToken { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = "https://api.trello.com";

    public string AgentCommentMarker { get; init; } = "<!-- agent-status -->";
}
