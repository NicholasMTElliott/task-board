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
    ILogger<UpdateFileProcessor> logger,
    ICardDependencyClient? dependencyClient = null)
{
    private readonly ICardDependencyClient _dependencyClient =
        dependencyClient ?? NullCardDependencyClient.Instance;

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

        var newTicketFiles = new List<(string FilePath, string Slug)>();
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
                    newTicketFiles.Add((filePath, newTicketMatch.Groups[1].Value));
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

        if (newTicketFiles.Count > 0)
        {
            var results = await ProcessNewTicketFilesAsync(
                newTicketFiles, sourceCardId, currentSourceComments, generationConfig, cancellationToken);
            createdTickets.AddRange(results);
        }

        return new UpdateProcessingResult(createdTickets, postedComments, referenceContent);
    }

    private async Task<IReadOnlyList<CreatedTicketInfo>> ProcessNewTicketFilesAsync(
        IReadOnlyList<(string FilePath, string Slug)> files,
        string sourceCardId,
        IReadOnlyList<CardComment> currentComments,
        GenerationConfig? generationConfig,
        CancellationToken ct)
    {
        var pending = new List<PendingTicketCreation>();
        foreach (var (filePath, slug) in files)
        {
            var parsed = await PrepareNewTicketFileAsync(
                filePath, slug, sourceCardId, currentComments, generationConfig, ct);
            if (parsed is not null)
                pending.Add(parsed);
        }

        var created = new List<CreatedTicketWithDependencies>();
        foreach (var p in pending)
        {
            try
            {
                var newCardId = await boardClient.CreateCardAsync(p.Request, ct);
                logger.LogInformation(
                    "Created card #{NewCardId} '{Title}' from update file '{Slug}' (parent={Parent}, type={Type}, column={Column})",
                    newCardId, p.Parsed.Title, p.Slug,
                    p.Request.ParentCardId ?? "(none)", p.Request.CardType ?? "(none)",
                    p.Request.TargetColumn ?? "(none)");

                if (generationConfig is null)
                {
                    var dedupMarker = $"<!-- agent-created-ticket:{p.Slug} -->";
                    var commentBody = $"**{agentIdentity.DisplayName}** created #{newCardId}: {p.Parsed.Title}";
                    await boardClient.UpsertAgentCommentAsync(sourceCardId, commentBody, dedupMarker, ct);
                }

                File.Delete(p.FilePath);

                double? estimateValue = null;
                if (p.Parsed.Estimate is not null && double.TryParse(p.Parsed.Estimate, CultureInfo.InvariantCulture, out var ev))
                    estimateValue = ev;

                created.Add(new CreatedTicketWithDependencies(
                    new CreatedTicketInfo(newCardId, p.Parsed.Title, p.Slug, estimateValue),
                    p.Parsed.BlockedBy ?? [],
                    p.Parsed.Blocks ?? []));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to create card from update file {File}", Path.GetFileName(p.FilePath));
            }
        }

        await ApplyDependencyLinksAsync(sourceCardId, created, ct);
        return created.Select(c => c.Info).ToList();
    }

    private async Task<PendingTicketCreation?> PrepareNewTicketFileAsync(
        string filePath,
        string slug,
        string sourceCardId,
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

        // Field-based card type discriminator: if workflowConfig.CardTypeField is set, write the
        // resolved type's display name (CardTypeDefinition.Name) into that project field.
        var resolvedTypeKey = generationConfig?.TargetType ?? parsed.Type;
        if (!string.IsNullOrWhiteSpace(workflowConfig.CardTypeField) && resolvedTypeKey is not null
            && workflowConfig.CardTypes is not null
            && workflowConfig.CardTypes.TryGetValue(resolvedTypeKey, out var resolvedTypeDef))
        {
            fieldValues ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            fieldValues[workflowConfig.CardTypeField!] = resolvedTypeDef.Name;
        }

        // Literal field values from generationConfig.setFields (explicit values win over copyFields).
        if (generationConfig?.SetFields is { Count: > 0 })
        {
            fieldValues ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in generationConfig.SetFields)
                fieldValues[k] = v;
        }

        var request = new CreateCardRequest(
            Title: parsed.Title,
            Body: parsed.Body,
            ParentCardId: parentId,
            CardType: typeLabel,
            TargetColumn: targetColumn,
            FieldValues: fieldValues);

        return new PendingTicketCreation(filePath, slug, parsed, request);
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

    private async Task ApplyDependencyLinksAsync(
        string sourceCardId,
        IReadOnlyList<CreatedTicketWithDependencies> created,
        CancellationToken ct)
    {
        if (created.Count == 0)
            return;

        var slugToId = created.ToDictionary(c => c.Info.Slug, c => c.Info.NewCardId, StringComparer.OrdinalIgnoreCase);
        var plannedEdges = new HashSet<(string Blocked, string Blocker)>();

        foreach (var item in created)
        {
            foreach (var depRef in item.BlockedBy)
            {
                var blockerId = ResolveDependencyRef(depRef, sourceCardId, slugToId);
                if (blockerId is null)
                {
                    logger.LogWarning(
                        "Skipping dependency for created card #{CardId}: could not resolve blockedBy '{Ref}'",
                        item.Info.NewCardId, depRef);
                    continue;
                }

                await TryAddDependencyAsync(item.Info.NewCardId, blockerId, plannedEdges, ct);
            }

            foreach (var depRef in item.Blocks)
            {
                var blockedId = ResolveDependencyRef(depRef, sourceCardId, slugToId);
                if (blockedId is null)
                {
                    logger.LogWarning(
                        "Skipping dependency for created card #{CardId}: could not resolve blocks '{Ref}'",
                        item.Info.NewCardId, depRef);
                    continue;
                }

                await TryAddDependencyAsync(blockedId, item.Info.NewCardId, plannedEdges, ct);
            }
        }
    }

    private async Task TryAddDependencyAsync(
        string blockedCardId,
        string blockerCardId,
        HashSet<(string Blocked, string Blocker)> plannedEdges,
        CancellationToken ct)
    {
        if (string.Equals(blockedCardId, blockerCardId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Skipping self-dependency on card #{CardId}", blockedCardId);
            return;
        }

        var edge = (blockedCardId, blockerCardId);
        if (plannedEdges.Contains((blockerCardId, blockedCardId)))
        {
            logger.LogWarning(
                "Skipping dependency #{Blocked} blocked by #{Blocker}: would create a simple cycle",
                blockedCardId, blockerCardId);
            return;
        }

        if (!plannedEdges.Add(edge))
            return;

        try
        {
            await _dependencyClient.AddBlockedByAsync(blockedCardId, blockerCardId, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to add dependency #{Blocked} blocked by #{Blocker}",
                blockedCardId, blockerCardId);
        }
    }

    private static string? ResolveDependencyRef(
        string raw,
        string sourceCardId,
        IReadOnlyDictionary<string, string> slugToId)
    {
        var value = raw.Trim().Trim('"', '\'');
        if (value.Length == 0)
            return null;

        if (string.Equals(value, "current", StringComparison.OrdinalIgnoreCase))
            return sourceCardId;

        if (value.StartsWith('#'))
            value = value[1..].Trim();

        if (value.All(char.IsAsciiDigit))
            return value;

        return slugToId.TryGetValue(value, out var cardId) ? cardId : null;
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

        if (parentCard is null || workflowConfig.CardTypes is null)
            return true;

        // Find the parent's type key. Prefer the field-based discriminator if configured,
        // then fall back to the label-based mechanism for boards that still use labels.
        string? parentTypeKey = null;

        if (!string.IsNullOrWhiteSpace(workflowConfig.CardTypeField)
            && parentCard.Metadata is not null
            && parentCard.Metadata.TryGetValue(workflowConfig.CardTypeField!, out var fieldValue)
            && !string.IsNullOrWhiteSpace(fieldValue))
        {
            foreach (var (typeName, typeDef) in workflowConfig.CardTypes)
            {
                if (string.Equals(typeDef.Name, fieldValue, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(typeName, fieldValue, StringComparison.OrdinalIgnoreCase))
                {
                    parentTypeKey = typeName;
                    break;
                }
            }
        }

        if (parentTypeKey is null && parentCard.Labels is { Count: > 0 })
        {
            foreach (var (typeName, typeDef) in workflowConfig.CardTypes)
            {
                if (string.IsNullOrWhiteSpace(typeDef.LabelPrefix)) continue;
                var expectedLabel = $"{typeDef.LabelPrefix}:{typeName}";
                if (parentCard.Labels.Contains(expectedLabel, StringComparer.OrdinalIgnoreCase))
                {
                    parentTypeKey = typeName;
                    break;
                }
            }
        }

        if (parentTypeKey is null)
            return true; // No type discriminator found — no restriction applies

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
            // Empty/null LabelPrefix means the card type opts out of labels entirely
            // (board uses a project field for type discrimination instead).
            if (string.IsNullOrWhiteSpace(typeDef.LabelPrefix))
                return null;

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
        var blockedBy = new List<string>();
        var blocks = new List<string>();
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

            string? listKey = null;
            foreach (var line in frontMatter.Split('\n'))
            {
                var l = line.Trim();
                if (l.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
                {
                    title = l["title:".Length..].Trim().Trim('"', '\'');
                    listKey = null;
                }
                else if (l.StartsWith("type:", StringComparison.OrdinalIgnoreCase))
                {
                    type = l["type:".Length..].Trim().Trim('"', '\'');
                    listKey = null;
                }
                else if (l.StartsWith("parent:", StringComparison.OrdinalIgnoreCase))
                {
                    parent = l["parent:".Length..].Trim().Trim('"', '\'');
                    listKey = null;
                }
                else if (l.StartsWith("targetColumn:", StringComparison.OrdinalIgnoreCase))
                {
                    targetColumn = l["targetColumn:".Length..].Trim().Trim('"', '\'');
                    listKey = null;
                }
                else if (l.StartsWith("estimate:", StringComparison.OrdinalIgnoreCase))
                {
                    estimate = l["estimate:".Length..].Trim().Trim('"', '\'');
                    listKey = null;
                }
                else if (l.StartsWith("blockedBy:", StringComparison.OrdinalIgnoreCase))
                {
                    listKey = "blockedBy";
                    AddInlineListValues(l["blockedBy:".Length..], blockedBy);
                }
                else if (l.StartsWith("blocks:", StringComparison.OrdinalIgnoreCase))
                {
                    listKey = "blocks";
                    AddInlineListValues(l["blocks:".Length..], blocks);
                }
                else if (l.StartsWith("- ", StringComparison.Ordinal) && listKey is not null)
                {
                    var value = l[2..].Trim().Trim('"', '\'');
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        if (listKey == "blockedBy")
                            blockedBy.Add(value);
                        else
                            blocks.Add(value);
                    }
                }
                else if (l.Length > 0)
                {
                    listKey = null;
                }
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
            : new ParsedNewTicket(title, body, type, parent, targetColumn, estimate, blockedBy, blocks);
    }

    private static void AddInlineListValues(string raw, List<string> target)
    {
        var value = raw.Trim();
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (value.StartsWith('[') && value.EndsWith(']'))
        {
            foreach (var part in value[1..^1].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var item = part.Trim().Trim('"', '\'');
                if (!string.IsNullOrWhiteSpace(item))
                    target.Add(item);
            }
            return;
        }

        target.Add(value.Trim('"', '\''));
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
    string? Estimate = null,
    IReadOnlyList<string>? BlockedBy = null,
    IReadOnlyList<string>? Blocks = null);

internal sealed record PendingTicketCreation(
    string FilePath,
    string Slug,
    ParsedNewTicket Parsed,
    CreateCardRequest Request);

internal sealed record CreatedTicketWithDependencies(
    CreatedTicketInfo Info,
    IReadOnlyList<string> BlockedBy,
    IReadOnlyList<string> Blocks);
