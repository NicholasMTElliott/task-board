namespace TaskBoard.Worker.Clients;

public sealed class TrelloClientOptions
{
    public const string SectionName = "Trello";

    public string ApiKey { get; init; } = string.Empty;

    public string ApiToken { get; init; } = string.Empty;

    public string BaseUrl { get; init; } = "https://api.trello.com";

    /// <summary>
    /// Optional override for the agent's Trello username used in {{agent}} template resolution.
    /// </summary>
    public string? AgentUsername { get; init; }
}
