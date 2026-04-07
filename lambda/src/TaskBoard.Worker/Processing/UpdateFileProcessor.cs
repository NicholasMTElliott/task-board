using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Result returned by <see cref="UpdateFileProcessor.ProcessUpdatesAsync"/>.
/// </summary>
public sealed record UpdateProcessingResult(
    int FilesProcessed,
    IReadOnlyList<string> CreatedCardIds);

/// <summary>
/// Represents a parsed new-ticket update file.
/// </summary>
internal sealed record ParsedNewTicket(
    string Title,
    string Body,
    string? Type = null,
    string? Parent = null,
    string? TargetColumn = null);

/// <summary>
/// Scans .aiboard/updates/ for new-{slug}.md files, creates cards on the board,
/// posts notification comments, and deletes processed files.
///
/// This is the file-based mechanism for agents to request new tickets.
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
    private const string UpdatesRelativePath = ".aiboard/updates";

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
            return new UpdateProcessingResult(0, []);

        var files = Directory.GetFiles(updatesDir, "new-*.md")
            .OrderBy(f => f)
            .ToList();

        if (files.Count == 0)
            return new UpdateProcessingResult(0, []);

        logger.LogInformation(
            "Processing {Count} update file(s) from '{Dir}' (step={Step})",
            files.Count, updatesDir, stepName);

        var createdIds = new List<string>();

        foreach (var filePath in files)
        {
            var slug = Path.GetFileNameWithoutExtension(filePath)["new-".Length..];
            try
            {
                var cardId = await ProcessNewTicketFileAsync(
                    filePath, slug, sourceCardId, stepName,
                    currentSourceComments, generationConfig, cancellationToken);

                if (cardId is not null)
                    createdIds.Add(cardId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to process update file '{File}' — skipping", filePath);
            }
        }

        return new UpdateProcessingResult(files.Count, createdIds);
    }

    private async Task<string?> ProcessNewTicketFileAsync(
        string filePath,
        string slug,
        string sourceCardId,
        string stepName,
        IReadOnlyList<CardComment> currentComments,
        GenerationConfig? generationConfig,
        CancellationToken ct)
    {
        // Dedup: if a comment with this slug marker already exists, card was already created
        var dedupMarker = $"<!-- agent-created-ticket:{slug} -->";
        if (currentComments.Any(c => c.Body.Contains(dedupMarker, StringComparison.Ordinal)))
        {
            logger.LogDebug("Skipping '{Slug}' — already created (dedup marker found)", slug);
            File.Delete(filePath);
            return null;
        }

        var content = await File.ReadAllTextAsync(filePath, ct);
        var parsed = ParseNewTicketFile(content);
        if (parsed is null)
        {
            logger.LogWarning("Could not parse new-ticket file '{File}' — skipping", filePath);
            File.Delete(filePath);
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

        // Post notification comment on the source card
        var commentBody = $"{dedupMarker}\n**{agentIdentity.DisplayName}** created #{newCardId}: {parsed.Title}";
        await boardClient.UpsertAgentCommentAsync(sourceCardId, commentBody, dedupMarker, ct);

        // Delete processed file
        File.Delete(filePath);

        return newCardId;
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
}
