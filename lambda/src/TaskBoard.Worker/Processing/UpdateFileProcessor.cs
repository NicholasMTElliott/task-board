using System.Text.RegularExpressions;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Processes update files written by agents to .aiboard/updates/.
/// Handles two file types:
///   new-{slug}.md      — creates a new ticket via ITaskBoardClient.CreateCardAsync
///   {cardId}-comment.md — posts a cross-card comment via ITaskBoardClient.UpsertAgentCommentAsync
/// </summary>
public sealed class UpdateFileProcessor(
    ITaskBoardClient boardClient,
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
        CancellationToken cancellationToken)
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
                        sourceCardId, stepName, currentSourceComments, cancellationToken);
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
        string filePath, string slug, string sourceCardId, string stepName,
        IReadOnlyList<CardComment> currentComments, CancellationToken ct)
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
            logger.LogWarning("Could not parse new ticket file {File}: missing or empty title in YAML front matter", Path.GetFileName(filePath));
            return null;
        }

        var (title, body) = parsed.Value;

        var newCardId = await boardClient.CreateCardAsync(title, body, ct);
        logger.LogInformation("Created ticket #{NewCardId} '{Title}' from update file (slug={Slug})", newCardId, title, slug);

        // Post notification comment on the original card so subsequent steps see the dedup marker
        var marker = $"<!-- agent-created-ticket:{slug} -->";
        var notification = $"{marker}\n**New ticket created:** #{newCardId} — {title}\n\n" +
            $"_Created during step `{stepName}` of card #{sourceCardId}_";
        await boardClient.UpsertAgentCommentAsync(sourceCardId, notification, marker, ct);

        File.Delete(filePath);

        return new CreatedTicketInfo(newCardId, title, slug);
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
        var commentBody = $"{marker}\n**Note from card #{sourceCardId} (step: {stepName}):**\n\n{content}";
        await boardClient.UpsertAgentCommentAsync(targetCardId, commentBody, marker, ct);

        logger.LogInformation("Posted cross-card comment on #{TargetCardId} from #{SourceCardId} step {Step}",
            targetCardId, sourceCardId, stepName);

        File.Delete(filePath);
        return new CrossCardCommentInfo(targetCardId, Path.GetFileName(filePath));
    }

    /// <summary>
    /// Parses a new-ticket file: YAML front matter for title, body after front matter.
    /// Returns null if the file format is invalid or title is missing.
    /// </summary>
    internal static (string Title, string Body)? ParseNewTicketFile(string content)
    {
        if (!content.StartsWith("---"))
            return null;

        var endIdx = content.IndexOf("---", 3, StringComparison.Ordinal);
        if (endIdx < 0)
            return null;

        var frontMatter = content[3..endIdx];
        var body = content[(endIdx + 3)..].TrimStart('\r', '\n');

        string? title = null;
        foreach (var line in frontMatter.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
            {
                title = trimmed["title:".Length..].Trim().Trim('"', '\'');
                break;
            }
        }

        return string.IsNullOrWhiteSpace(title) ? null : (title!, body);
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
