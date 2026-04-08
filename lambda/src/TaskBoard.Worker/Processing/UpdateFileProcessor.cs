using System.Globalization;
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
    IImageUploader imageUploader,
    ILogger<UpdateFileProcessor> logger)
{
    internal const string UpdatesRelativePath = ".aiboard/updates";

    private static readonly Regex NewTicketPattern =
        new(@"^new-(.+)\.md$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CommentPattern =
        new(@"^(\d+)-comment\.md$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReferencePattern =
        new(@"^(\d+)-reference\.md$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
        string? referenceContent = null;

        foreach (var filePath in files)
        {
            try
            {
                var fileName = Path.GetFileName(filePath);

                var newTicketMatch = NewTicketPattern.Match(fileName);
                if (newTicketMatch.Success)
                {
                    var result = await ProcessNewTicketFileAsync(
                        filePath, newTicketMatch.Groups[1].Value, workspacePath,
                        sourceCardId, stepName, currentSourceComments, generationConfig, cancellationToken);
                    if (result is not null)
                        createdTickets.Add(result);
                    continue;
                }

                var commentMatch = CommentPattern.Match(fileName);
                if (commentMatch.Success)
                {
                    var result = await ProcessCommentFileAsync(
                        filePath, commentMatch.Groups[1].Value, workspacePath,
                        sourceCardId, stepName, cancellationToken);
                    if (result is not null)
                        postedComments.Add(result);
                    continue;
                }

                var referenceMatch = ReferencePattern.Match(fileName);
                if (referenceMatch.Success)
                {
                    var content = await File.ReadAllTextAsync(filePath, cancellationToken);
                    File.Delete(filePath);
                    logger.LogInformation("Read reference content from {File} ({Length} chars)",
                        fileName, content.Length);
                    referenceContent = content;
                    continue;
                }

                logger.LogWarning("Unrecognized update file (does not match new-{{slug}}.md, {{cardId}}-comment.md, or {{cardId}}-reference.md): {FileName}", fileName);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to process update file {File}", Path.GetFileName(filePath));
            }
        }

        return new UpdateProcessingResult(createdTickets, postedComments, referenceContent);
    }

    private async Task<CreatedTicketInfo?> ProcessNewTicketFileAsync(
        string filePath,
        string slug,
        string workspacePath,
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

        // AllowedChildren enforcement: when generationConfig is set, verify the parent card's
        // type allows generating the target type. Skips creation with a warning if not allowed.
        if (generationConfig is not null && workflowConfig.CardTypes is not null)
        {
            if (!await IsTargetTypeAllowedAsync(sourceCardId, generationConfig.TargetType, ct))
            {
                File.Delete(filePath);
                return null;
            }
        }

        // Process any local image references in the ticket body before creating the card
        var processedBody = await ImageReferenceProcessor.ProcessLocalImagesAsync(
            parsed.Body, workspacePath, imageUploader, sourceCardId, logger, ct);

        // Build CreateCardRequest: generationConfig values take precedence, front matter as fallback
        var typeLabel = BuildTypeLabel(generationConfig?.TargetType ?? parsed.Type);
        var parentId = generationConfig?.LinkToParent == true
            ? sourceCardId
            : parsed.Parent;
        var targetColumn = generationConfig?.TargetColumn ?? parsed.TargetColumn;

        // Build field values: copy fields from parent + estimate from front matter
        Dictionary<string, string>? fieldValues = null;
        if (generationConfig?.CopyFields is { Count: > 0 })
        {
            try
            {
                var parentCard = await boardClient.GetCardAsync(sourceCardId, ct);
                if (parentCard.Metadata is not null)
                {
                    foreach (var field in generationConfig.CopyFields)
                    {
                        if (parentCard.Metadata.TryGetValue(field, out var value) && !string.IsNullOrEmpty(value))
                        {
                            fieldValues ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            fieldValues[field] = value;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not fetch parent card #{ParentCardId} to copy fields — proceeding without", sourceCardId);
            }
        }

        if (parsed.Estimate is not null)
        {
            fieldValues ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            fieldValues[workflowConfig.Estimation?.FieldName ?? "Estimate"] = parsed.Estimate;
        }

        var request = new CreateCardRequest(
            Title: parsed.Title,
            Body: processedBody,
            ParentCardId: parentId,
            CardType: typeLabel,
            TargetColumn: targetColumn,
            FieldValues: fieldValues);

        var newCardId = await boardClient.CreateCardAsync(request, ct);
        logger.LogInformation(
            "Created card #{NewCardId} '{Title}' from update file '{Slug}' (parent={Parent}, type={Type}, column={Column})",
            newCardId, parsed.Title, slug,
            parentId ?? "(none)", typeLabel ?? "(none)", targetColumn ?? "(none)");

        // Post notification comment on the source card with dedup marker (skip for structured
        // generation — the task list in the parent body already tracks the relationship)
        if (generationConfig is null)
        {
            var dedupMarker = $"<!-- agent-created-ticket:{slug} -->";
            var commentBody = $"**{agentIdentity.DisplayName}** created #{newCardId}: {parsed.Title}";
            await boardClient.UpsertAgentCommentAsync(sourceCardId, commentBody, dedupMarker, ct);
        }

        // Delete processed file
        File.Delete(filePath);

        // Parse estimate as double for aggregation
        double? estimateValue = null;
        if (parsed.Estimate is not null && double.TryParse(parsed.Estimate, CultureInfo.InvariantCulture, out var ev))
            estimateValue = ev;

        return new CreatedTicketInfo(newCardId, parsed.Title, slug, estimateValue);
    }

    private async Task<CrossCardCommentInfo?> ProcessCommentFileAsync(
        string filePath, string targetCardId, string workspacePath, string sourceCardId, string stepName,
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

        var processedContent = await ImageReferenceProcessor.ProcessLocalImagesAsync(
            content, workspacePath, imageUploader, sourceCardId, logger, ct);

        var marker = $"<!-- agent-cross-comment:{sourceCardId}:{stepName} -->";
        var commentBody = $"**Note from card #{sourceCardId} (step: {stepName}):**\n\n{processedContent}";
        await boardClient.UpsertAgentCommentAsync(targetCardId, commentBody, marker, ct);

        logger.LogInformation("Posted cross-card comment on #{TargetCardId} from #{SourceCardId} step {Step}",
            targetCardId, sourceCardId, stepName);

        File.Delete(filePath);
        return new CrossCardCommentInfo(targetCardId, Path.GetFileName(filePath));
    }

    /// <summary>
    /// Returns false if the parent card's type explicitly restricts child types and the
    /// requested targetType is not in the allowedChildren list.
    /// Returns true in all other cases (no parent found, no type label, no restriction configured).
    /// </summary>
    private async Task<bool> IsTargetTypeAllowedAsync(string parentCardId, string targetType, CancellationToken ct)
    {
        BoardCard? parentCard;
        try
        {
            parentCard = await boardClient.GetCardAsync(parentCardId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch parent card #{ParentCardId} to check allowedChildren — proceeding with creation", parentCardId);
            return true;
        }

        if (parentCard?.Labels is null || workflowConfig.CardTypes is null)
            return true;

        // Find the parent's type key by matching its labels to cardTypes label conventions
        string? parentTypeKey = null;
        foreach (var (typeName, typeDef) in workflowConfig.CardTypes)
        {
            var expectedLabel = $"{typeDef.LabelPrefix}:{typeName}";
            if (parentCard.Labels.Contains(expectedLabel, StringComparer.OrdinalIgnoreCase))
            {
                parentTypeKey = typeName;
                break;
            }
        }

        if (parentTypeKey is null)
            return true; // No type label found — no restriction applies

        if (!workflowConfig.CardTypes.TryGetValue(parentTypeKey, out var parentTypeDef))
            return true;

        if (parentTypeDef.AllowedChildren is null)
            return true; // Null means no restriction defined for this type

        // AllowedChildren is defined (including empty list) — targetType must be explicitly listed
        if (parentTypeDef.AllowedChildren.Contains(targetType, StringComparer.OrdinalIgnoreCase))
            return true;

        logger.LogWarning(
            "Skipping ticket creation: parent card #{ParentCardId} has type '{ParentType}' " +
            "which does not allow child type '{TargetType}'. AllowedChildren: [{Allowed}]",
            parentCardId, parentTypeKey, targetType,
            string.Join(", ", parentTypeDef.AllowedChildren));
        return false;
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
    /// Front matter fields: title (required), type, parent, targetColumn, estimate.
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
        string? estimate = null;
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
                else if (l.StartsWith("estimate:", StringComparison.OrdinalIgnoreCase))
                    estimate = l["estimate:".Length..].Trim().Trim('"', '\'');
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
            : new ParsedNewTicket(title, body, type, parent, targetColumn, estimate);
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
    IReadOnlyList<CrossCardCommentInfo> PostedComments,
    string? ReferenceContent = null)
{
    public static readonly UpdateProcessingResult Empty = new([], []);
    public bool HasUpdates => CreatedTickets.Count > 0 || PostedComments.Count > 0;

    /// <summary>
    /// Sum of all created ticket estimates (from front matter). Null if no estimates were provided.
    /// </summary>
    public double? TotalEstimate
    {
        get
        {
            double sum = 0;
            bool any = false;
            foreach (var t in CreatedTickets)
            {
                if (t.Estimate.HasValue)
                {
                    sum += t.Estimate.Value;
                    any = true;
                }
            }
            return any ? sum : null;
        }
    }
}

public sealed record CreatedTicketInfo(string NewCardId, string Title, string Slug, double? Estimate = null);
public sealed record CrossCardCommentInfo(string TargetCardId, string SourceFileName);

/// <summary>
/// Represents a parsed new-ticket update file.
/// </summary>
internal sealed record ParsedNewTicket(
    string Title,
    string Body,
    string? Type = null,
    string? Parent = null,
    string? TargetColumn = null,
    string? Estimate = null);
