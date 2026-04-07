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
            ["issue", "view", cardId, "--repo", _options.Repo, "--json",
             "number,title,body,projectItems,labels,assignees"],
            cancellationToken);

        using var doc = JsonDocument.Parse(issueJson);
        var root = doc.RootElement;

        var id = root.GetProperty("number").GetInt32().ToString();
        var title = root.GetProperty("title").GetString() ?? "";
        var body = root.GetProperty("body").GetString() ?? "";

        // Resolve column (Status field) from projectItems
        var columnId = ResolveColumnFromProjectItems(root, _options.StatusFieldName);

        var metadata = new Dictionary<string, string> { ["issueNumber"] = id };

        var labels = root.TryGetProperty("labels", out var labelsProp)
            ? labelsProp.EnumerateArray()
                .Select(l => l.TryGetProperty("name", out var n) ? n.GetString() : null)
                .Where(n => n is not null)
                .Select(n => n!)
                .ToList()
            : null;

        var assignees = root.TryGetProperty("assignees", out var assigneesProp)
            ? assigneesProp.EnumerateArray()
                .Select(a => a.TryGetProperty("login", out var l) ? l.GetString() : null)
                .Where(l => l is not null)
                .Select(l => l!)
                .ToList()
            : null;

        return new BoardCard(id, title, body, columnId, metadata, labels, assignees);
    }

    public async Task<IReadOnlyList<BoardCard>> GetBoardCardsAsync(string boardId, CancellationToken cancellationToken, IReadOnlyList<string>? excludeStatuses = null)
    {
        const int fetchLimit = 500;

        // boardId = project number
        // Use gh project item-list with explicit limit (default is only 30)
        var args = new List<string>
        {
            "project", "item-list", boardId, "--owner", _options.Owner,
            "--limit", fetchLimit.ToString()
        };

        // Filter out excluded statuses at the API level to keep the working set small
        if (excludeStatuses is { Count: > 0 })
        {
            var query = string.Join(' ', excludeStatuses.Select(s => $"-status:{s}"));
            args.AddRange(["--query", query]);
        }

        args.AddRange(["--format", "json"]);

        var json = await RunGhAsync(args.ToArray(), cancellationToken);

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

            // Parse additional custom fields (e.g., Priority) into metadata
            foreach (var prop in item.EnumerateObject())
            {
                if (prop.Name is "id" or "content" or "status")
                    continue;
                if (prop.Value.ValueKind == JsonValueKind.String)
                    metadata[prop.Name] = prop.Value.GetString() ?? "";
            }

            // Fetch labels and assignees from the issue REST API
            IReadOnlyList<string>? labels = null;
            IReadOnlyList<string>? assignees = null;
            try
            {
                var issueJson = await RunGhAsync(
                    ["api", $"repos/{_options.Repo}/issues/{id}", "--jq",
                     "{labels: [.labels[].name], assignees: [.assignees[].login]}"],
                    cancellationToken);
                using var issueDoc = JsonDocument.Parse(issueJson);
                var issueRoot = issueDoc.RootElement;
                if (issueRoot.TryGetProperty("labels", out var lp))
                    labels = lp.EnumerateArray()
                        .Select(l => l.GetString())
                        .Where(l => l is not null)
                        .Select(l => l!)
                        .ToList();
                if (issueRoot.TryGetProperty("assignees", out var ap))
                    assignees = ap.EnumerateArray()
                        .Select(a => a.GetString())
                        .Where(a => a is not null)
                        .Select(a => a!)
                        .ToList();
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Could not fetch labels/assignees for issue {Id} — filter predicates will see empty lists", id);
            }

            cards.Add(new BoardCard(id, title, body, columnId, metadata, labels, assignees));
        }

        logger.LogDebug("Fetched {Count} cards from GitHub project {ProjectNumber}", cards.Count, boardId);

        if (cards.Count >= fetchLimit)
            logger.LogWarning(
                "Card fetch returned {Count} items (limit {Limit}) — some items may be missing. "
                + "Consider increasing the fetch limit or adding more API-level filters.",
                cards.Count, fetchLimit);

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

    public async Task<string> CreateCardAsync(string title, string body, CancellationToken cancellationToken)
    {
        // Write body to a temp file to avoid shell escaping issues with large content
        var tempFile = Path.GetTempFileName();
        string issueUrl;
        try
        {
            await File.WriteAllTextAsync(tempFile, body, cancellationToken);
            issueUrl = await RunGhAsync(
                ["issue", "create", "--repo", _options.Repo, "--title", title, "--body-file", tempFile],
                cancellationToken);
            issueUrl = issueUrl.Trim();
        }
        finally
        {
            File.Delete(tempFile);
        }

        // Parse issue number from URL: "https://github.com/owner/repo/issues/42"
        var issueNumber = issueUrl.Split('/').Last();
        logger.LogInformation("Created issue #{IssueNumber} '{Title}'", issueNumber, title);

        // Best-effort: add to project board
        try
        {
            await RunGhAsync(
                ["project", "item-add", _options.ProjectNumber.ToString(),
                 "--owner", _options.Owner, "--url", issueUrl],
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to add issue #{IssueNumber} to project — issue exists but is not on the board", issueNumber);
        }

        return issueNumber;
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

        // Execute the mutation using GraphQL variables to avoid interpolation
        const string mutation = """
            mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $optionId: String!) {
              updateProjectV2ItemFieldValue(input: {
                projectId: $projectId
                itemId: $itemId
                fieldId: $fieldId
                value: { singleSelectOptionId: $optionId }
              }) {
                projectV2Item { id }
              }
            }
            """;

        await RunGhAsync(["api", "graphql",
            "-f", $"query={mutation}",
            "-f", $"projectId={projectId}",
            "-f", $"itemId={itemId}",
            "-f", $"fieldId={fieldId}",
            "-f", $"optionId={optionId}"], cancellationToken);
        logger.LogInformation("Moved issue {IssueNumber} to status {Status}", cardId, columnId);
    }

    public async Task UpsertAgentCommentAsync(string cardId, string commentBody, string commentMarker, CancellationToken cancellationToken)
    {
        var markedBody = $"{commentMarker}\n{commentBody}";

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
            if (body is not null && body.Contains(commentMarker, StringComparison.Ordinal))
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
        const string query = """
            query($owner: String!, $projectNumber: Int!, $fieldName: String!) {
              user(login: $owner) {
                projectV2(number: $projectNumber) {
                  id
                  field(name: $fieldName) {
                    ... on ProjectV2SingleSelectField {
                      id
                      options { id name }
                    }
                  }
                }
              }
            }
            """;

        var json = await RunGhAsync(["api", "graphql",
            "-f", $"query={query}",
            "-f", $"owner={_options.Owner}",
            "-F", $"projectNumber={projectNumber}",
            "-f", $"fieldName={_options.StatusFieldName}"], cancellationToken);
        using var doc = JsonDocument.Parse(json);
        ThrowOnGraphQlErrors(doc, $"ResolveStatusFieldOption({statusName})");

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
        const string query = """
            query($repoOwner: String!, $repoName: String!, $issueNumber: Int!) {
              repository(owner: $repoOwner, name: $repoName) {
                issue(number: $issueNumber) {
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

        var json = await RunGhAsync(["api", "graphql",
            "-f", $"query={query}",
            "-f", $"repoOwner={repoParts[0]}",
            "-f", $"repoName={repoParts[1]}",
            "-F", $"issueNumber={issueNumber}"], cancellationToken);
        using var doc = JsonDocument.Parse(json);
        ThrowOnGraphQlErrors(doc, $"ResolveProjectItemId({issueNumber})");

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
                // status can be a plain string or an object with a "name" property
                if (statusProp.ValueKind == JsonValueKind.String)
                    return statusProp.GetString() ?? "";
                if (statusProp.ValueKind == JsonValueKind.Object && statusProp.TryGetProperty("name", out var nameProp))
                    return nameProp.GetString() ?? "";
            }
        }

        return "";
    }

    public async Task AddLabelAsync(string cardId, string labelName, CancellationToken cancellationToken)
    {
        await RunGhAsync(
            ["issue", "edit", cardId, "--repo", _options.Repo, "--add-label", labelName],
            cancellationToken);
        logger.LogInformation("Added label '{Label}' to issue {IssueNumber}", labelName, cardId);
    }

    public async Task RemoveLabelAsync(string cardId, string labelName, CancellationToken cancellationToken)
    {
        await RunGhAsync(
            ["issue", "edit", cardId, "--repo", _options.Repo, "--remove-label", labelName],
            cancellationToken);
        logger.LogInformation("Removed label '{Label}' from issue {IssueNumber}", labelName, cardId);
    }

    public async Task AssignAsync(string cardId, string username, CancellationToken cancellationToken)
    {
        await RunGhAsync(
            ["issue", "edit", cardId, "--repo", _options.Repo, "--add-assignee", username],
            cancellationToken);
        logger.LogInformation("Assigned '{User}' to issue {IssueNumber}", username, cardId);
    }

    public async Task UnassignAsync(string cardId, string? username, CancellationToken cancellationToken)
    {
        if (username is not null)
        {
            await RunGhAsync(
                ["issue", "edit", cardId, "--repo", _options.Repo, "--remove-assignee", username],
                cancellationToken);
            logger.LogInformation("Unassigned '{User}' from issue {IssueNumber}", username, cardId);
        }
        else
        {
            // Remove all current assignees
            var issueJson = await RunGhAsync(
                ["issue", "view", cardId, "--repo", _options.Repo, "--json", "assignees"],
                cancellationToken);
            using var doc = JsonDocument.Parse(issueJson);
            var assignees = doc.RootElement.GetProperty("assignees")
                .EnumerateArray()
                .Select(a => a.TryGetProperty("login", out var login) ? login.GetString() : null)
                .Where(l => l is not null)
                .ToList();

            if (assignees.Count > 0)
            {
                var args = new List<string>
                    { "issue", "edit", cardId, "--repo", _options.Repo };
                foreach (var a in assignees)
                {
                    args.Add("--remove-assignee");
                    args.Add(a!);
                }
                await RunGhAsync([.. args], cancellationToken);
                logger.LogInformation("Removed all {Count} assignee(s) from issue {IssueNumber}", assignees.Count, cardId);
            }
        }
    }

    public async Task SetFieldAsync(string cardId, string fieldName, string value, CancellationToken cancellationToken)
    {
        // Resolve the project item and field option IDs, then execute GraphQL mutation
        var (projectId, fieldId, optionId) = await ResolveStatusFieldOption(
            _options.ProjectNumber, value, cancellationToken);
        var itemId = await ResolveProjectItemId(cardId, projectId, cancellationToken);

        // Reuse the same parameterized mutation as MoveCardToColumnAsync
        const string setFieldMutation = """
            mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!, $optionId: String!) {
              updateProjectV2ItemFieldValue(input: {
                projectId: $projectId
                itemId: $itemId
                fieldId: $fieldId
                value: { singleSelectOptionId: $optionId }
              }) {
                projectV2Item { id }
              }
            }
            """;

        await RunGhAsync(["api", "graphql",
            "-f", $"query={setFieldMutation}",
            "-f", $"projectId={projectId}",
            "-f", $"itemId={itemId}",
            "-f", $"fieldId={fieldId}",
            "-f", $"optionId={optionId}"], cancellationToken);
        logger.LogInformation("Set field '{Field}' to '{Value}' on issue {IssueNumber}", fieldName, value, cardId);
    }

    public async Task ClearFieldAsync(string cardId, string fieldName, CancellationToken cancellationToken)
    {
        var (projectId, fieldId, _) = await ResolveStatusFieldOption(
            _options.ProjectNumber, fieldName, cancellationToken);
        var itemId = await ResolveProjectItemId(cardId, projectId, cancellationToken);

        const string clearMutation = """
            mutation($projectId: ID!, $itemId: ID!, $fieldId: ID!) {
              clearProjectV2ItemFieldValue(input: {
                projectId: $projectId
                itemId: $itemId
                fieldId: $fieldId
              }) {
                projectV2Item { id }
              }
            }
            """;

        await RunGhAsync(["api", "graphql",
            "-f", $"query={clearMutation}",
            "-f", $"projectId={projectId}",
            "-f", $"itemId={itemId}",
            "-f", $"fieldId={fieldId}"], cancellationToken);
        logger.LogInformation("Cleared field '{Field}' on issue {IssueNumber}", fieldName, cardId);
    }

    public async Task<string> GetCurrentUserAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.AgentUsername))
            return _options.AgentUsername;

        var json = await RunGhAsync(["api", "user", "--jq", ".login"], cancellationToken);
        return json.Trim().Trim('"');
    }

    internal static string? ExtractCommentIdFromUrl(string url)
    {
        // https://github.com/{owner}/{repo}/issues/{number}#issuecomment-{id}
        var marker = "#issuecomment-";
        var idx = url.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0) return null;
        var suffix = url[(idx + marker.Length)..];
        // Validate the extracted ID is purely numeric
        return suffix.Length > 0 && suffix.All(char.IsAsciiDigit) ? suffix : null;
    }

    /// <summary>
    /// Checks a parsed GraphQL JSON response for an "errors" array and throws if present.
    /// GraphQL can return HTTP 200 with errors; <c>gh</c> exit code won't catch these.
    /// </summary>
    private static void ThrowOnGraphQlErrors(JsonDocument doc, string context)
    {
        if (doc.RootElement.TryGetProperty("errors", out var errors)
            && errors.ValueKind == JsonValueKind.Array
            && errors.GetArrayLength() > 0)
        {
            var messages = errors.EnumerateArray()
                .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : e.ToString())
                .ToList();
            throw new InvalidOperationException(
                $"GraphQL error ({context}): {string.Join("; ", messages)}");
        }
    }

    private async Task<string> RunGhAsync(
        string[] args, CancellationToken cancellationToken, string? stdinData = null)
    {
        var opName = $"gh {string.Join(" ", args.Take(3))}";
        return await RetryHelper.ExecuteWithRetryAsync(
            () => RunGhCoreAsync(args, stdinData, cancellationToken),
            IsTransientGhError,
            maxRetries: 2,
            logger,
            opName,
            cancellationToken);
    }

    private async Task<string> RunGhCoreAsync(
        string[] args, string? stdinData, CancellationToken cancellationToken)
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
            if (stderr.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("abuse detection", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("secondary rate", StringComparison.OrdinalIgnoreCase)
                || stderr.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase))
            {
                throw new RateLimitException(
                    $"GitHub API rate limit hit. gh exited with code {process.ExitCode}. stderr: {stderr}");
            }

            throw new InvalidOperationException(
                $"gh exited with code {process.ExitCode}. stderr: {stderr}");
        }

        return stdout;
    }

    /// <summary>
    /// Determines if a gh CLI error is transient and worth retrying.
    /// Rate limit errors are NOT retried here — they're handled at the polling level.
    /// </summary>
    private static bool IsTransientGhError(Exception ex)
    {
        if (ex is RateLimitException) return false;

        var msg = ex.Message;
        return msg.Contains("HTTP 500", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("HTTP 502", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("HTTP 503", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("connection refused", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("connection reset", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("ETIMEDOUT", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("ECONNRESET", StringComparison.OrdinalIgnoreCase)
            || msg.Contains("socket hang up", StringComparison.OrdinalIgnoreCase);
    }
}
