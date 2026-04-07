using System.Text;
using System.Text.RegularExpressions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

public sealed class TaskFileManager(ILogger<TaskFileManager> logger)
{
    private const string TasksRelativePath = ".aiboard/tasks";

    public async Task WriteAllTaskFilesAsync(
        string workspacePath,
        IReadOnlyList<BoardCard> cards,
        WorkflowConfig workflowConfig,
        ReferenceAnnotationContext? referenceContext = null,
        CancellationToken cancellationToken = default)
    {
        var tasksDir = Path.Combine(workspacePath, TasksRelativePath);
        Directory.CreateDirectory(tasksDir);

        foreach (var card in cards)
        {
            var listName = workflowConfig.States.TryGetValue(card.ColumnId, out var state)
                ? state.Name
                : "Unknown";

            var content = BuildTaskFileContent(card, listName, referenceContext);
            var filePath = GetTaskFilePath(workspacePath, card.Id, card.Title);

            await File.WriteAllTextAsync(filePath, content, Encoding.UTF8, cancellationToken);
        }

        logger.LogInformation("Wrote {Count} task files to {Path}", cards.Count, tasksDir);
    }

    public async Task<string> ReadTaskFileAsync(
        string workspacePath, string cardId, string? title = null, CancellationToken cancellationToken = default)
    {
        var filePath = GetTaskFilePath(workspacePath, cardId, title);
        return await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken);
    }

    public static string GetTaskFilePath(string workspacePath, string cardId, string? title = null)
    {
        var fileName = GetTaskFileName(cardId, title);
        return Path.Combine(workspacePath, TasksRelativePath, fileName);
    }

    public static string GetTaskFileName(string cardId, string? title = null)
    {
        var slug = SlugHelper.Sanitize(title);
        return string.IsNullOrEmpty(slug) ? $"{cardId}.md" : $"{cardId}-{slug}.md";
    }

    internal static string BuildTaskFileContent(
        BoardCard card, string listName, ReferenceAnnotationContext? referenceContext = null)
    {
        var body = card.Body;

        if (referenceContext is not null && card.Id == referenceContext.TargetCardId)
        {
            // Separate text refs (have OriginalText) from non-text refs
            var textRefs = referenceContext.References
                .Where(r => r.OriginalText is not null)
                .ToList();
            var nonTextRefs = referenceContext.References
                .Where(r => r.OriginalText is null)
                .ToList();

            body = AnnotateBodyWithReferences(body, textRefs, referenceContext.CardIdToFilePath);

            var crossRefSection = BuildCrossReferencesSection(nonTextRefs, referenceContext.CardIdToFilePath);
            if (crossRefSection is not null)
                body = body + "\n\n" + crossRefSection;
        }

        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"id: {card.Id}");
        sb.AppendLine($"title: {EscapeYamlValue(card.Title)}");
        sb.AppendLine($"list: {listName}");
        sb.AppendLine($"list_id: {card.ColumnId}");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(body);
        return sb.ToString();
    }

    internal static string AnnotateBodyWithReferences(
        string body,
        IReadOnlyList<CardReference> textReferences,
        IReadOnlyDictionary<string, string> cardIdToFilePath)
    {
        if (string.IsNullOrEmpty(body) || textReferences.Count == 0)
            return body;

        // Collect all (index, length, annotation) tuples, then apply from end to start
        var replacements = new List<(int Index, int Length, string Replacement)>();

        foreach (var textRef in textReferences)
        {
            if (textRef.OriginalText is null)
                continue;
            if (!cardIdToFilePath.TryGetValue(textRef.ReferencedCardId, out var filePath))
                continue;

            var searchFrom = 0;
            while (true)
            {
                var idx = body.IndexOf(textRef.OriginalText, searchFrom, StringComparison.Ordinal);
                if (idx < 0)
                    break;

                // Avoid substring matching: e.g. "#5" should not match inside "#50"
                var afterIdx = idx + textRef.OriginalText.Length;
                if (afterIdx < body.Length && char.IsDigit(body[afterIdx]))
                {
                    searchFrom = afterIdx;
                    continue;
                }

                replacements.Add((idx, textRef.OriginalText.Length,
                    $"{textRef.OriginalText} ( see {filePath} )"));
                searchFrom = afterIdx;
            }
        }

        // Sort by index descending so replacements don't shift earlier indices
        replacements.Sort((a, b) => b.Index.CompareTo(a.Index));

        var sb = new StringBuilder(body);
        foreach (var (index, length, replacement) in replacements)
        {
            sb.Remove(index, length);
            sb.Insert(index, replacement);
        }

        return sb.ToString();
    }

    internal static string? BuildCrossReferencesSection(
        IReadOnlyList<CardReference> references,
        IReadOnlyDictionary<string, string> cardIdToFilePath)
    {
        var resolved = references
            .Where(r => cardIdToFilePath.ContainsKey(r.ReferencedCardId))
            .ToList();

        if (resolved.Count == 0)
            return null;

        var sb = new StringBuilder("## Cross-References\n\n");

        foreach (var r in resolved)
        {
            var filePath = cardIdToFilePath[r.ReferencedCardId];
            var label = FormatReferenceTypeLabel(r.ReferenceType);
            var cardLabel = r.Title is not null
                ? $"#{r.ReferencedCardId} {r.Title}"
                : $"#{r.ReferencedCardId}";
            sb.AppendLine($"- **{label}**: {cardLabel} ( see {filePath} )");
        }

        return sb.ToString();
    }

    internal static string StripAnnotations(string body)
    {
        // Strip inline annotations: ( see .aiboard/tasks/....md )
        body = Regex.Replace(body, @"\s*\(\s*see\s+\.aiboard/tasks/[^)]+\.md\s*\)", "");

        // Strip the ## Cross-References section (always at end)
        body = Regex.Replace(body, @"\n*## Cross-References\n[\s\S]*$", "");

        return body;
    }

    private static string FormatReferenceTypeLabel(string referenceType)
    {
        // "sub_item" -> "Sub-item", "parent_item" -> "Parent-item", etc.
        return referenceType.Replace('_', '-') switch
        {
            var s when s.Length > 0 => char.ToUpperInvariant(s[0]) + s[1..],
            _ => referenceType
        };
    }

    /// <summary>
    /// Extracts the body content from a task file, stripping the YAML front matter.
    /// </summary>
    public static string ExtractBodyFromTaskFile(string taskFileContent)
    {
        if (!taskFileContent.StartsWith("---"))
            return taskFileContent;

        // Find the closing "---" of the front matter (skip the opening "---")
        var endOfFrontMatter = taskFileContent.IndexOf("---", 3, StringComparison.Ordinal);
        if (endOfFrontMatter < 0)
            return taskFileContent;

        var afterFrontMatter = taskFileContent[(endOfFrontMatter + 3)..];

        // Strip the leading newline(s) between front matter and body
        return afterFrontMatter.TrimStart('\r', '\n');
    }

    public static string GetCommentsFilePath(string workspacePath, string cardId, string? title = null)
    {
        var taskFilePath = GetTaskFilePath(workspacePath, cardId, title);
        return Path.ChangeExtension(taskFilePath, null) + "-comments.md";
    }

    public async Task WriteCommentsFileAsync(
        string workspacePath,
        string cardId,
        string? title,
        IReadOnlyList<CardComment> comments,
        CancellationToken cancellationToken)
    {
        var ordered = comments.OrderBy(c => c.CreatedAt).ToList();

        var humanComments = new List<CardComment>();
        var agentComments = new List<CardComment>();

        foreach (var comment in ordered)
        {
            var isAgent = ContainsAgentMarker(comment.Body);
            var stripped = StripHtmlMarkers(comment.Body);
            if (string.IsNullOrWhiteSpace(stripped))
                continue;

            if (isAgent)
            {
                var withoutLog = StripConversationLog(stripped);
                if (!string.IsNullOrWhiteSpace(withoutLog))
                    agentComments.Add(comment with { Body = withoutLog });
            }
            else
            {
                humanComments.Add(comment with { Body = stripped });
            }
        }

        if (humanComments.Count == 0 && agentComments.Count == 0)
        {
            logger.LogDebug("No comments to write for card {CardId} after filtering", cardId);
            return;
        }

        var filePath = GetCommentsFilePath(workspacePath, cardId, title);
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine("type: comments");
        sb.AppendLine($"card_id: {cardId}");
        sb.AppendLine("note: \"READ-ONLY. Do not modify this file.\"");
        sb.AppendLine("---");

        if (humanComments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# Reviewer Directives");
            sb.AppendLine();
            sb.AppendLine("_These comments are from the human project operator and are authoritative._");
            AppendComments(sb, humanComments);
        }

        if (agentComments.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("# Agent History");
            AppendComments(sb, agentComments);
        }

        await File.WriteAllTextAsync(filePath, sb.ToString(), Encoding.UTF8, cancellationToken);
        logger.LogInformation(
            "Wrote comments file for card {CardId}: {HumanCount} reviewer, {AgentCount} agent",
            cardId, humanComments.Count, agentComments.Count);
    }

    private static void AppendComments(StringBuilder sb, List<CardComment> comments)
    {
        foreach (var comment in comments)
        {
            sb.AppendLine();
            sb.AppendLine($"### {comment.Author} — {comment.CreatedAt:yyyy-MM-dd HH:mm} UTC");
            sb.AppendLine();
            sb.AppendLine(comment.Body);
            sb.AppendLine();
            sb.AppendLine("---");
        }
    }

    private static string StripHtmlMarkers(string body)
        => Regex.Replace(body, @"<!--.*?-->", "", RegexOptions.Singleline).Trim();

    private static string StripConversationLog(string body)
        => Regex.Replace(
            body,
            @"<details>\s*<summary>Agent conversation log</summary>[\s\S]*?</details>",
            "",
            RegexOptions.None).Trim();

    /// <summary>
    /// Classifies a comment as agent-generated by checking for known orchestrator marker patterns.
    /// All agent comments are posted via UpsertAgentCommentAsync which always prepends one of these markers.
    /// Known patterns: agent-step:, agent-run:, gate-check:, merge-run:, agent-rate-limit:
    /// </summary>
    internal static bool ContainsAgentMarker(string body)
        => body.Contains("<!-- agent-step:", StringComparison.Ordinal)
        || body.Contains("<!-- agent-run:", StringComparison.Ordinal)
        || body.Contains("<!-- gate-check:", StringComparison.Ordinal)
        || body.Contains("<!-- merge-run:", StringComparison.Ordinal)
        || body.Contains("<!-- agent-rate-limit:", StringComparison.Ordinal);

    internal static string EscapeYamlValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "\"\"";

        // Quote if value contains characters that could confuse YAML parsers
        if (value.Contains(':') || value.Contains('#') || value.Contains('"')
            || value.Contains('\'') || value.Contains('\n')
            || value.StartsWith(' ') || value.EndsWith(' '))
        {
            return "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        }

        return value;
    }
}
