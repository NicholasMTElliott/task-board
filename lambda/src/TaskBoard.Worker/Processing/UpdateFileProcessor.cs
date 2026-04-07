using System.Text.RegularExpressions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Processes update files written by agents to .aiboard/updates/.
/// Handles two file types:
///   new-{slug}.md      — creates a new ticket via ITaskBoardClient.CreateCardAsync
///   {cardId}-comment.md — posts a cross-card comment via ITaskBoardClient.UpsertAgentCommentAsync
///
/// For structured generation steps (generationConfig on a WorkflowStep),
/// the orchestrator applies type/parent/column metadata from the step config.
/// For ad-hoc creation (no generationConfig), agents can optionally specify
/// metadata in the YAML front matter of the update file.
/// </summary>
public sealed class UpdateFileProcessor(
    ITaskBoardClient boardClient,
    WorkflowConfig workflowConfig,
    AgentIdentity agentIdentity,
    ILogger<UpdateFileProcessor> logger)
{
    internal const string UpdatesRelativePath = ".aiboard/updates";

    private static readonly Regex NewTicketPattern =
        new(@"^new-(.+)\.md$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CommentPattern =
        new(@"^(\d+)-comment\.md$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Scans the updates directory in the workspace, processes all recognized .md files,
    /// and returns a summary of actions taken.
    /// </summary>
    public async Task<UpdateProcessingResult> ProcessUpdatesAsync(
        string workspacePath,
        string sourceCardId,
        string stepName,
        IReadOnlyList<CardComment> currentSourceComments,
        CancellationToken cancellationToken,
        GenerationConfig? generationConfig = null)
    {
        var updatesDir = Path.Combine(workspacePath, UpdatesRelativePath);
        if (!Directory.Exists(updatesDir))
            return UpdateProcessingResult.Empty;

        var files = Directory.GetFiles(updatesDir, "*.md");
        if (files.Length == 0)
            return UpdateProcessingResult.Empty;

        var createdTickets = new List<CreatedTicketInfo>();
        var postedComments = new List<CrossCardCommentInfo>();

        foreach (var filePath in files)
        {
            try
            {
                var fileName = Path.GetFileName(filePath);

                var newTicketMatch = NewTicketPattern.Match(fileName);
                if (newTicketMatch.Success)
                {
                    var result = await ProcessNewTicketFileAsync(
                        filePath, newTicketMatch.Groups[1].Value,
                        sourceCardId, stepName, currentSourceComments, generationConfig, cancellationToken);
                    if (result is not null)
                        createdTickets.Add(result);
                    continue;
                }

                var commentMatch = CommentPattern.Match(fileName);
                if (commentMatch.Success)
                {
                    var result = await ProcessCommentFileAsync(
                        filePath, commentMatch.Groups[1].Value,
                        sourceCardId, stepName, cancellationToken);
                    if (result is not null)
                        postedComments.Add(result);
                    continue;
                }

                logger.LogWarning("Unrecognized update file (does not match new-{{slug}}.md or {{cardId}}-comment.md): {FileName}", fileName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to process update file {File}", Path.GetFileName(filePath));
            }
        }

        return new UpdateProcessingResult(createdTickets, postedComments);
    }

    private async Task<CreatedTicketInfo?> ProcessNewTicketFileAsync(
        string filePath,
        string slug,
        string sourceCardId,
        string stepName,
        IReadOnlyList<CardComment> currentComments,
        GenerationConfig? generationConfig,
        CancellationToken ct)
    {
        // Dedup check: if a ticket with this slug was already created in a prior step, skip
        if (HasCreatedTicketMarker(currentComments, slug))
        {
            logger.LogInformation("Skipping already-created ticket with slug '{Slug}' (marker found in comments)", slug);
            File.Delete(filePath);
            return null;
        }

        var content = await File.ReadAllTextAsync(filePath, ct);
        var parsed = ParseNewTicketFile(content);
        if (parsed is null)
        {
            logger.LogWarning("Could not parse new-ticket file '{File}' — skipping", filePath);
            return null;
        }

        // Build CreateCardRequest: generationConfig values take precedence, front matter as fallback
        var typeLabel = BuildTypeLabel(generationConfig?.TargetType ?? parsed.Type);
        var parentId = generationConfig?.LinkToParent == true
            ? sourceCardId
            : parsed.Parent;
        var targetColumn = generationConfig?.TargetColumn ?? parsed.TargetColumn;

        var request = new CreateCardRequest(
            Title: parsed.Title,
            Body: parsed.Body,
            ParentCardId: parentId,
            CardType: typeLabel,
            TargetColumn: targetColumn);

        var newCardId = await boardClient.CreateCardAsync(request, ct);
        logger.LogInformation(
            "Created card #{NewCardId} '{Title}' from update file '{Slug}' (parent={Parent}, type={Type}, column={Column})",
            newCardId, parsed.Title, slug,
            parentId ?? "(none)", typeLabel ?? "(none)", targetColumn ?? "(none)");

        // Post notification comment on the source card with dedup marker
        var dedupMarker = $"<!-- agent-created-ticket:{slug} -->";
        var commentBody = $"**{agentIdentity.DisplayName}** created #{newCardId}: {parsed.Title}";
        await boardClient.UpsertAgentCommentAsync(sourceCardId, commentBody, dedupMarker, ct);

        // Delete processed file
        File.Delete(filePath);

        return new CreatedTicketInfo(newCardId, parsed.Title, slug);
    }

    private async Task<CrossCardCommentInfo?> ProcessCommentFileAsync(
        string filePath, string targetCardId, string sourceCardId, string stepName,
        CancellationToken ct)
    {
        if (targetCardId == sourceCardId)
        {
            logger.LogWarning("Skipping cross-card comment targeting the source card #{CardId} — use the task file instead", targetCardId);
            File.Delete(filePath);
            return null;
        }

        var content = await File.ReadAllTextAsync(filePath, ct);
        if (string.IsNullOrWhiteSpace(content))
        {
            logger.LogWarning("Empty cross-card comment file for card #{CardId}", targetCardId);
            File.Delete(filePath);
            return null;
        }

        var marker = $"<!-- agent-cross-comment:{sourceCardId}:{stepName} -->";
        var commentBody = $"**Note from card #{sourceCardId} (step: {stepName}):**\n\n{content}";
        await boardClient.UpsertAgentCommentAsync(targetCardId, commentBody, marker, ct);

        logger.LogInformation("Posted cross-card comment on #{TargetCardId} from #{SourceCardId} step {Step}",
            targetCardId, sourceCardId, stepName);

        File.Delete(filePath);
        return new CrossCardCommentInfo(targetCardId, Path.GetFileName(filePath));
    }

    /// <summary>
    /// Formats a type label from a raw type string, using the cardTypes config for the label prefix.
    /// Falls back to "type:{rawType}" if the type is not in the config.
    /// </summary>
    internal string? BuildTypeLabel(string? rawType)
    {
        if (rawType is null) return null;

        if (workflowConfig.CardTypes is not null
            && workflowConfig.CardTypes.TryGetValue(rawType, out var typeDef))
        {
            return $"{typeDef.LabelPrefix}:{rawType}";
        }

        // Fallback: use the default prefix "type"
        return $"type:{rawType}";
    }

    /// <summary>
    /// Parses a new-ticket update file with optional YAML front matter.
    /// Front matter fields: title (required), type, parent, targetColumn.
    /// Body is everything after the closing "---".
    /// </summary>
    internal static ParsedNewTicket? ParseNewTicketFile(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var trimmed = content.TrimStart();

        string? title = null;
        string? type = null;
        string? parent = null;
        string? targetColumn = null;
        string body;

        if (trimmed.StartsWith("---", StringComparison.Ordinal))
        {
            // Find closing ---
            var endIdx = trimmed.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (endIdx < 0)
            {
                // No closing front matter — treat whole file as body, extract title from H1
                body = trimmed;
                title = ExtractTitleFromBody(body);
                return string.IsNullOrWhiteSpace(title) ? null : new ParsedNewTicket(title, body);
            }

            var frontMatter = trimmed[3..endIdx];
            body = trimmed[(endIdx + 4)..].TrimStart('\n', '\r');

            foreach (var line in frontMatter.Split('\n'))
            {
                var l = line.Trim();
                if (l.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
                    title = l["title:".Length..].Trim().Trim('"', '\'');
                else if (l.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
                    type = l["type:".Length..].Trim().Trim('"', '\'');
                else if (l.StartsWith("parent:", StringComparison.OrdinalIgnoreCase))
                    parent = l["parent:".Length..].Trim().Trim('"', '\'');
                else if (l.StartsWith("targetColumn:", StringComparison.OrdinalIgnoreCase))
                    targetColumn = l["targetColumn:".Length..].Trim().Trim('"', '\'');
            }
        }
        else
        {
            // No front matter — extract title from first H1
            body = trimmed;
            title = ExtractTitleFromBody(body);
        }

        return string.IsNullOrWhiteSpace(title)
            ? null
            : new ParsedNewTicket(title, body, type, parent, targetColumn);
    }

    private static string? ExtractTitleFromBody(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            var l = line.Trim();
            if (l.StartsWith("# ", StringComparison.Ordinal))
                return l[2..].Trim();
        }
        return null;
    }

    /// <summary>
    /// Returns true if any comment in the list contains the created-ticket marker for the given slug.
    /// Used to prevent duplicate ticket creation across steps.
    /// </summary>
    internal static bool HasCreatedTicketMarker(IReadOnlyList<CardComment> comments, string slug)
    {
        var marker = $"<!-- agent-created-ticket:{slug} -->";
        return comments.Any(c => c.Body.Contains(marker, StringComparison.Ordinal));
    }
}

public sealed record UpdateProcessingResult(
    IReadOnlyList<CreatedTicketInfo> CreatedTickets,
    IReadOnlyList<CrossCardCommentInfo> PostedComments)
{
    public static readonly UpdateProcessingResult Empty = new([], []);
    public bool HasUpdates => CreatedTickets.Count > 0 || PostedComments.Count > 0;
}

public sealed record CreatedTicketInfo(string NewCardId, string Title, string Slug);
public sealed record CrossCardCommentInfo(string TargetCardId, string SourceFileName);

/// <summary>
/// Represents a parsed new-ticket update file.
/// </summary>
internal sealed record ParsedNewTicket(
    string Title,
    string Body,
    string? Type = null,
    string? Parent = null,
    string? TargetColumn = null);
