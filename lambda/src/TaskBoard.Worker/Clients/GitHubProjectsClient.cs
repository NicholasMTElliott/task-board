using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TaskBoard.Worker.Clients;

/// <summary>
/// ITaskBoardClient implementation for GitHub Projects v2.
/// Uses the `gh` CLI for API calls (GraphQL and REST).
///
/// Card identity: cardId = issue number (string). The client resolves
/// the project item node ID internally for project-level mutations.
///
/// boardId format: "{projectNumber}" — owner comes from GitHubProjectsOptions.
/// </summary>
public sealed class GitHubProjectsClient(
    IOptions<GitHubProjectsOptions> options,
    ILogger<GitHubProjectsClient> logger) : ITaskBoardClient
{
    private readonly GitHubProjectsOptions _options = options.Value;

    public async Task<BoardCard> GetCardAsync(string cardId, CancellationToken cancellationToken)
    {
        // cardId is the issue number — fetch the issue directly
        var issueJson = await RunGhAsync(
            ["issue", "view", cardId, "--repo", _options.Repo, "--json", "number,title,body,projectItems"],
            cancellationToken);

        using var doc = JsonDocument.Parse(issueJson);
        var root = doc.RootElement;

        var id = root.GetProperty("number").GetInt32().ToString();
        var title = root.GetProperty("title").GetString() ?? "";
        var body = root.GetProperty("body").GetString() ?? "";

        // Resolve column (Status field) from projectItems
        var columnId = ResolveColumnFromProjectItems(root, _options.StatusFieldName);

        var metadata = new Dictionary<string, string> { ["issueNumber"] = id };

        return new BoardCard(id, title, body, columnId, metadata);
    }

    public async Task<IReadOnlyList<BoardCard>> GetBoardCardsAsync(string boardId, CancellationToken cancellationToken)
    {
        // boardId = project number
        // Use gh project item-list to get all items
        var json = await RunGhAsync(
            ["project", "item-list", boardId, "--owner", _options.Owner, "--format", "json"],
            cancellationToken);

        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("items");

        var cards = new List<BoardCard>();
        foreach (var item in items.EnumerateArray())
        {
            var content = item.GetProperty("content");

            // Skip draft issues without a number (or handle them if needed)
            if (!content.TryGetProperty("number", out var numberProp))
                continue;

            var id = numberProp.GetInt32().ToString();
            var title = content.TryGetProperty("title", out var titleProp)
                ? titleProp.GetString() ?? ""
                : "";
            var body = content.TryGetProperty("body", out var bodyProp)
                ? bodyProp.GetString() ?? ""
                : "";

            // Status field value
            var columnId = "";
            if (item.TryGetProperty("status", out var statusProp))
            {
                columnId = statusProp.GetString() ?? "";
            }

            var metadata = new Dictionary<string, string>
            {
                ["issueNumber"] = id,
            };

            // Include projectItemId if available
            if (item.TryGetProperty("id", out var itemIdProp))
            {
                metadata["projectItemId"] = itemIdProp.GetString() ?? "";
            }

            cards.Add(new BoardCard(id, title, body, columnId, metadata));
        }

        logger.LogInformation("Fetched {Count} cards from GitHub project {ProjectNumber}", cards.Count, boardId);
        return cards;
    }

    public async Task UpdateCardBodyAsync(string cardId, string body, CancellationToken cancellationToken)
    {
        // Write body to a temp file to avoid shell escaping issues with large content
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, body, cancellationToken);
            await RunGhAsync(
                ["issue", "edit", cardId, "--repo", _options.Repo, "--body-file", tempFile],
                cancellationToken);
            logger.LogInformation("Updated body for issue {IssueNumber} ({Length} chars)", cardId, body.Length);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    public async Task MoveCardToColumnAsync(string cardId, string columnId, CancellationToken cancellationToken)
    {
        // To move a card, we need the project item node ID and the status field option ID.
        // columnId in workflow config is the status option name (e.g., "Design", "Done").
        // We need to:
        // 1. Get the project node ID, field ID, and option ID for the target status
        // 2. Get the project item ID for this issue
        // 3. Mutation: updateProjectV2ItemFieldValue

        var projectNumber = _options.ProjectNumber;

        // Get project metadata (field IDs, option IDs)
        var (projectId, fieldId, optionId) = await ResolveStatusFieldOption(
            projectNumber, columnId, cancellationToken);

        // Get the item ID for this issue in the project
        var itemId = await ResolveProjectItemId(cardId, projectId, cancellationToken);

        // Execute the mutation
        var mutation = $$"""
            mutation {
              updateProjectV2ItemFieldValue(input: {
                projectId: "{{projectId}}"
                itemId: "{{itemId}}"
                fieldId: "{{fieldId}}"
                value: { singleSelectOptionId: "{{optionId}}" }
              }) {
                projectV2Item { id }
              }
            }
            """;

        await RunGhAsync(["api", "graphql", "-f", $"query={mutation}"], cancellationToken);
        logger.LogInformation("Moved issue {IssueNumber} to status {Status}", cardId, columnId);
    }

    public async Task UpsertAgentCommentAsync(string cardId, string commentBody, CancellationToken cancellationToken)
    {
        var markedBody = $"{_options.CommentMarker}\n{commentBody}";

        // List existing comments and find one with our marker
        var commentsJson = await RunGhAsync(
            ["issue", "view", cardId, "--repo", _options.Repo, "--json", "comments"],
            cancellationToken);

        using var doc = JsonDocument.Parse(commentsJson);
        var comments = doc.RootElement.GetProperty("comments");

        string? existingCommentUrl = null;
        foreach (var comment in comments.EnumerateArray())
        {
            var body = comment.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() : null;
            if (body is not null && body.Contains(_options.CommentMarker, StringComparison.Ordinal))
            {
                existingCommentUrl = comment.TryGetProperty("url", out var urlProp)
                    ? urlProp.GetString()
                    : null;
                break;
            }
        }

        if (existingCommentUrl is not null)
        {
            // Edit existing comment — extract comment ID from URL
            // URL format: https://github.com/{owner}/{repo}/issues/{number}#issuecomment-{id}
            var commentId = ExtractCommentIdFromUrl(existingCommentUrl);
            if (commentId is not null)
            {
                // Use the REST API to edit the comment
                var editEndpoint = $"repos/{_options.Repo}/issues/comments/{commentId}";
                var bodyJson = JsonSerializer.Serialize(new { body = markedBody });
                await RunGhAsync(
                    ["api", editEndpoint, "--method", "PATCH", "--input", "-"],
                    cancellationToken,
                    stdinData: bodyJson);
                logger.LogInformation("Updated agent comment {CommentId} on issue {IssueNumber}", commentId, cardId);
                return;
            }
        }

        // Create new comment
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, markedBody, cancellationToken);
            await RunGhAsync(
                ["issue", "comment", cardId, "--repo", _options.Repo, "--body-file", tempFile],
                cancellationToken);
            logger.LogInformation("Created new agent comment on issue {IssueNumber}", cardId);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    public async Task<IReadOnlyList<CardComment>> GetCardCommentsAsync(string cardId, CancellationToken cancellationToken)
    {
        var json = await RunGhAsync(
            ["api", $"repos/{_options.Repo}/issues/{cardId}/comments", "--paginate"],
            cancellationToken);

        using var doc = JsonDocument.Parse(json);
        var comments = new List<CardComment>();

        foreach (var item in doc.RootElement.EnumerateArray())
        {
            var author = item.TryGetProperty("user", out var user)
                && user.TryGetProperty("login", out var login)
                ? login.GetString() ?? "unknown"
                : "unknown";
            var body = item.TryGetProperty("body", out var bodyProp)
                ? bodyProp.GetString() ?? ""
                : "";
            var createdAt = item.TryGetProperty("created_at", out var createdProp)
                ? DateTimeOffset.Parse(createdProp.GetString()!)
                : DateTimeOffset.MinValue;

            comments.Add(new CardComment(author, body, createdAt));
        }

        comments.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        logger.LogInformation("Fetched {Count} comments for issue {IssueNumber}", comments.Count, cardId);
        return comments;
    }

    private async Task<string> GetProjectNumberFromIssue(string issueNumber, CancellationToken cancellationToken)
    {
        // For now, we use the project items from the issue view.
        // The caller should provide this via options or boardId. Use a simple approach.
        var json = await RunGhAsync(
            ["issue", "view", issueNumber, "--repo", _options.Repo, "--json", "projectItems"],
            cancellationToken);

        using var doc = JsonDocument.Parse(json);
        // Return the first project found, or fall back to options
        // This is a simplification — in practice the caller's boardId should be authoritative
        return doc.RootElement.GetProperty("projectItems").EnumerateArray()
            .Select(p => p.TryGetProperty("number", out var n) ? n.GetInt32().ToString() : null)
            .FirstOrDefault(n => n is not null) ?? "";
    }

    private async Task<(string ProjectId, string FieldId, string OptionId)> ResolveStatusFieldOption(
        string projectNumber, string statusName, CancellationToken cancellationToken)
    {
        var query = $$"""
            query {
              user(login: "{{_options.Owner}}") {
                projectV2(number: {{projectNumber}}) {
                  id
                  field(name: "{{_options.StatusFieldName}}") {
                    ... on ProjectV2SingleSelectField {
                      id
                      options { id name }
                    }
                  }
                }
              }
            }
            """;

        var json = await RunGhAsync(["api", "graphql", "-f", $"query={query}"], cancellationToken);
        using var doc = JsonDocument.Parse(json);

        var project = doc.RootElement.GetProperty("data").GetProperty("user").GetProperty("projectV2");
        var projectId = project.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Could not resolve project ID");

        var field = project.GetProperty("field");
        var fieldId = field.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Could not resolve Status field ID");

        var optionId = field.GetProperty("options").EnumerateArray()
            .Where(o => string.Equals(o.GetProperty("name").GetString(), statusName, StringComparison.OrdinalIgnoreCase))
            .Select(o => o.GetProperty("id").GetString())
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Status option '{statusName}' not found in project");

        return (projectId, fieldId, optionId);
    }

    private async Task<string> ResolveProjectItemId(
        string issueNumber, string projectId, CancellationToken cancellationToken)
    {
        // gh issue view --json projectItems doesn't include the node ID.
        // Query the project directly via GraphQL to find the item for this issue.
        var repoParts = _options.Repo.Split('/');
        var query = $$"""
            query {
              repository(owner: "{{repoParts[0]}}", name: "{{repoParts[1]}}") {
                issue(number: {{issueNumber}}) {
                  projectItems(first: 10) {
                    nodes {
                      id
                      project { id }
                    }
                  }
                }
              }
            }
            """;

        var json = await RunGhAsync(["api", "graphql", "-f", $"query={query}"], cancellationToken);
        using var doc = JsonDocument.Parse(json);

        var nodes = doc.RootElement
            .GetProperty("data").GetProperty("repository").GetProperty("issue")
            .GetProperty("projectItems").GetProperty("nodes");

        foreach (var node in nodes.EnumerateArray())
        {
            var nodeProjectId = node.GetProperty("project").GetProperty("id").GetString();
            if (nodeProjectId == projectId)
            {
                return node.GetProperty("id").GetString()
                    ?? throw new InvalidOperationException("Null project item ID");
            }
        }

        // Fall back to first item if project ID doesn't match (single-project case)
        foreach (var node in nodes.EnumerateArray())
        {
            return node.GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Null project item ID");
        }

        throw new InvalidOperationException(
            $"Issue {issueNumber} is not linked to any project. Add it to the project first.");
    }

    private static string ResolveColumnFromProjectItems(JsonElement root, string statusFieldName)
    {
        if (!root.TryGetProperty("projectItems", out var items))
            return "";

        foreach (var item in items.EnumerateArray())
        {
            if (item.TryGetProperty("status", out var statusProp))
            {
                return statusProp.GetString() ?? "";
            }
        }

        return "";
    }

    internal static string? ExtractCommentIdFromUrl(string url)
    {
        // https://github.com/{owner}/{repo}/issues/{number}#issuecomment-{id}
        var marker = "#issuecomment-";
        var idx = url.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        return url[(idx + marker.Length)..];
    }

    private async Task<string> RunGhAsync(
        string[] args, CancellationToken cancellationToken, string? stdinData = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinData is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        logger.LogDebug("Running: gh {Args}", string.Join(" ", args));

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start gh process");

        if (stdinData is not null)
        {
            await process.StandardInput.WriteAsync(stdinData);
            process.StandardInput.Close();
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"gh exited with code {process.ExitCode}. stderr: {stderr}");
        }

        return stdout;
    }
}
