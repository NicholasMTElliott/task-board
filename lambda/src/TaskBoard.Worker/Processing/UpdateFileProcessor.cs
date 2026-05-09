using System.Globalization;
using System.Linq;
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
    ICardDependencyClient? dependencyClient = null,
    // Rerun redesign Problem 2: optional router. When wired, the
    // created-ticket-dedupe and cross-card notification comments use the
    // new aiboard-log marker shape (kind:created_ticket_dedupe → upsert,
    // kind:cross_card_notification → delete_and_repost). When null, direct
    // fallback calls still use the aiboard-log marker grammar.
    ICommentRouter? commentRouter = null)
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

        // Order is significant: same-batch dependency resolution and cycle
        // detection in TryAddDependencyAsync depend on whichever new-*.md file
        // is processed first "winning" the directional edge. Sort alphabetically
        // so the outcome is deterministic across filesystems (NTFS happens to
        // return name-ordered; ext4 with dir_index does not).
        var files = Directory.GetFiles(updatesDir, "*.md");
        if (files.Length == 0)
            return UpdateProcessingResult.Empty;
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        var newTicketFiles = new List<(string FilePath, string Slug)>();
        var createdTickets = new List<CreatedTicketInfo>();
        var postedComments = new List<CrossCardCommentInfo>();
        var unrecognizedFiles = new List<UnrecognizedUpdateFile>();
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

                // Unrecognized .md file. Most common case is a missing `new-`
                // prefix on what was supposed to be a new-ticket file (KvA / eve
                // failure shape: agent writes `apply-theme-class.md` instead
                // of `new-apply-theme-class.md` and the regex silently rejects
                // it). Detect that case and call it out specifically — it's a
                // 99% chance to be the cause.
                var likelyMissingNewPrefix = LooksLikeMissingNewPrefix(fileName);
                if (likelyMissingNewPrefix)
                {
                    logger.LogWarning(
                        "Update file '{FileName}' looks like a new-ticket file with the required `new-` prefix missing. " +
                        "The orchestrator only matches files named `new-{{slug}}.md` — rename to `new-{FileName}` to get this " +
                        "ticket created. Skipping.",
                        fileName, fileName);
                }
                else
                {
                    logger.LogWarning(
                        "Unrecognized update file (does not match new-{{slug}}.md, {{cardId}}-comment.md, or {{cardId}}-reference.md): {FileName}",
                        fileName);
                }
                unrecognizedFiles.Add(new UnrecognizedUpdateFile(fileName, likelyMissingNewPrefix));
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

        // End-of-loop summary: a single Warning that surfaces the count
        // visibly even when individual file warnings scroll off. Loud-failure
        // mode for the case where EVERY .md file was rejected — that almost
        // always means the agent got the filename convention wrong, and it's
        // a silent-success failure mode if we don't shout about it.
        if (unrecognizedFiles.Count > 0)
        {
            var likelyMissingPrefixCount = unrecognizedFiles.Count(u => u.LikelyMissingNewPrefix);
            if (createdTickets.Count == 0 && postedComments.Count == 0 && referenceContent is null)
            {
                logger.LogWarning(
                    "UpdateFileProcessor scanned {Total} .md file(s) in .aiboard/updates/ for card {CardId} step '{StepName}' " +
                    "and recognized ZERO of them ({MissingPrefixCount} look like new-ticket files missing the `new-` prefix). " +
                    "No tickets were created. The agent likely violated the filename convention — review prompt or model output.",
                    unrecognizedFiles.Count, sourceCardId, stepName, likelyMissingPrefixCount);
            }
            else
            {
                logger.LogWarning(
                    "UpdateFileProcessor for card {CardId} step '{StepName}': {Total} unrecognized .md file(s) skipped " +
                    "({MissingPrefixCount} look like new-ticket files missing the `new-` prefix). Filenames: {Names}",
                    sourceCardId, stepName, unrecognizedFiles.Count, likelyMissingPrefixCount,
                    string.Join(", ", unrecognizedFiles.Select(u => u.FileName)));
            }
        }

        return new UpdateProcessingResult(createdTickets, postedComments, referenceContent, unrecognizedFiles);
    }

    /// <summary>
    /// Heuristic: a filename "looks like" a new-ticket file with the missing `new-` prefix when
    ///   - it ends in .md
    ///   - it has at least one hyphen (slug shape)
    ///   - it does not match the comment-file (`{N}-comment.md`) or reference-file (`{N}-reference.md`) patterns
    ///   - it doesn't start with a digit (a leading digit is the comment/reference shape)
    /// Conservative: prefers false-negative (silent skip with generic warning) over false-positive
    /// (telling an operator to rename a file that wasn't a new-ticket file).
    /// </summary>
    internal static bool LooksLikeMissingNewPrefix(string fileName)
    {
        if (!fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return false;
        if (fileName.Length < 4) // ".md" + at least one char
            return false;
        if (CommentPattern.IsMatch(fileName) || ReferencePattern.IsMatch(fileName))
            return false;
        // Already has the prefix — if it does, the regex match would have caught it.
        if (fileName.StartsWith("new-", StringComparison.OrdinalIgnoreCase))
            return false;
        // Comment / reference patterns start with a digit. Don't claim those.
        if (char.IsDigit(fileName[0]))
            return false;
        // A hyphen anywhere suggests slug shape.
        return fileName.IndexOf('-') > 0;
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
                    var commentBody = $"**{agentIdentity.DisplayName}** created #{newCardId}: {p.Parsed.Title}";
                    if (commentRouter is not null)
                    {
                        // kind:created_ticket_dedupe → upsert (true dedupe key,
                        // not a status notice). The slug is the dedupe identity.
                        var marker = AiboardLogMarker.Build(
                            AiboardLogMarker.KindCreatedTicketDedupe,
                            new[] { KeyValuePair.Create("slug", p.Slug) });
                        await commentRouter.PostAsync(
                            sourceCardId, AiboardLogMarker.KindCreatedTicketDedupe,
                            commentBody, marker, ct);
                    }
                    else
                    {
                        var marker = AiboardLogMarker.Build(
                            AiboardLogMarker.KindCreatedTicketDedupe,
                            new[] { KeyValuePair.Create("slug", p.Slug) });
                        await boardClient.UpsertAgentCommentAsync(sourceCardId, commentBody, marker, ct);
                    }
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

        var commentBody = $"**Note from card #{sourceCardId} (step: {stepName}):**\n\n{content}";
        if (commentRouter is not null)
        {
            // kind:cross_card_notification → delete_and_repost. Each retry of
            // the source step replaces the earlier cross-card note with a
            // fresh one at the bottom of the target card's timeline.
            var newMarker = AiboardLogMarker.Build(
                AiboardLogMarker.KindCrossCardNotification,
                new[]
                {
                    KeyValuePair.Create("source_card", sourceCardId),
                    KeyValuePair.Create("source_step", stepName),
                });
            await commentRouter.PostAsync(
                targetCardId, AiboardLogMarker.KindCrossCardNotification,
                commentBody, newMarker, ct);
        }
        else
        {
            var marker = AiboardLogMarker.Build(
                AiboardLogMarker.KindCrossCardNotification,
                new[]
                {
                    KeyValuePair.Create("source_card", sourceCardId),
                    KeyValuePair.Create("source_step", stepName),
                });
            await boardClient.UpsertAgentCommentAsync(targetCardId, commentBody, marker, ct);
        }

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
        // Direct A↔B back-references only. Longer cycles (A→B→C→A) are not
        // detected here; the dependency provider (e.g. GitHub Issues) is the
        // source of truth and rejects deeper cycles itself.
        if (plannedEdges.Contains((blockerCardId, blockedCardId)))
        {
            logger.LogWarning(
                "Skipping dependency #{Blocked} blocked by #{Blocker}: would create a direct cycle",
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
    /// Used to prevent duplicate ticket creation across steps. Accepts both the
    /// legacy <c>&lt;!-- agent-created-ticket:{slug} --&gt;</c> shape AND the new
    /// <c>aiboard-log kind:created_ticket_dedupe slug:{slug}</c> shape so the
    /// dedupe lookup keeps working across the rerun-redesign migration: a
    /// step that previously created the ticket with a legacy marker won't
    /// silently re-create it after the migration switches the emitter.
    /// </summary>
    internal static bool HasCreatedTicketMarker(IReadOnlyList<CardComment> comments, string slug)
    {
        var legacyMarker = $"<!-- agent-created-ticket:{slug} -->";
        // The new marker has form `<!-- aiboard-log kind:created_ticket_dedupe slug:VALUE -->`.
        // Slug values may be quoted or bare depending on AiboardLogMarker.AppendValue's
        // logic — check both forms.
        var newMarkerBare = $"kind:created_ticket_dedupe slug:{slug} ";
        var newMarkerQuoted = $"kind:created_ticket_dedupe slug:\"{slug}\" ";
        return comments.Any(c =>
            c.Body.Contains(legacyMarker, StringComparison.Ordinal)
            || c.Body.Contains(newMarkerBare, StringComparison.Ordinal)
            || c.Body.Contains(newMarkerQuoted, StringComparison.Ordinal));
    }
}

public sealed record UpdateProcessingResult(
    IReadOnlyList<CreatedTicketInfo> CreatedTickets,
    IReadOnlyList<CrossCardCommentInfo> PostedComments,
    string? ReferenceContent = null,
    IReadOnlyList<UnrecognizedUpdateFile>? UnrecognizedFiles = null)
{
    public static readonly UpdateProcessingResult Empty = new([], []);
    public bool HasUpdates => CreatedTickets.Count > 0 || PostedComments.Count > 0;
    public IReadOnlyList<UnrecognizedUpdateFile> UnrecognizedFilesList =>
        UnrecognizedFiles ?? Array.Empty<UnrecognizedUpdateFile>();

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
/// A `.md` file in `.aiboard/updates/` that did not match any of the recognized
/// patterns (`new-{slug}.md`, `{N}-comment.md`, `{N}-reference.md`). Surfaced
/// in <see cref="UpdateProcessingResult"/> so callers (e.g. gate checks) can
/// see how the agent's output was rejected.
/// </summary>
/// <param name="FileName">Just the filename portion, no path.</param>
/// <param name="LikelyMissingNewPrefix">
/// True when the heuristic in <see cref="UpdateFileProcessor.LooksLikeMissingNewPrefix"/>
/// suggests this is a new-ticket file with the required prefix omitted (the
/// dominant agent-error mode).
/// </param>
public sealed record UnrecognizedUpdateFile(string FileName, bool LikelyMissingNewPrefix);

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
