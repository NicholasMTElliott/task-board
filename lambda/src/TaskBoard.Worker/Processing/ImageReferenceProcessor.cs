using System.Text.RegularExpressions;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Scans outbound markdown for local image references written by agents to .aiboard/images/output/,
/// uploads them via IImageUploader, and replaces local paths with hosted URLs before board write-back.
/// On upload failure, replaces the reference with a placeholder rather than leaving a broken path.
/// </summary>
internal static partial class ImageReferenceProcessor
{
    // Matches markdown image syntax referencing the agent image output directory:
    //   ![alt text](.aiboard/images/output/filename.ext)
    [GeneratedRegex(@"!\[([^\]]*)\]\(\.aiboard/images/output/([^)]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex LocalImagePattern();

    private static readonly Regex LocalImageRegex = LocalImagePattern();

    /// <summary>
    /// Finds all local .aiboard/images/output/ references in <paramref name="markdown"/>,
    /// uploads each file via <paramref name="uploader"/>, and returns the markdown with
    /// local paths replaced by hosted URLs.
    /// </summary>
    public static async Task<string> ProcessLocalImagesAsync(
        string markdown,
        string workspacePath,
        IImageUploader uploader,
        string cardId,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(markdown))
            return markdown;

        var matches = LocalImageRegex.Matches(markdown);
        if (matches.Count == 0)
            return markdown;

        // First pass: upload each unique file path once
        // Key: relative image path (e.g. "diagram.png"); Value: hosted URL or null on failure
        var pathToUrl = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in matches)
        {
            var relativeImagePath = match.Groups[2].Value;
            if (pathToUrl.ContainsKey(relativeImagePath))
                continue;

            var normalizedRelative = relativeImagePath.Replace('/', Path.DirectorySeparatorChar);
            var localFilePath = Path.Combine(workspacePath, ".aiboard", "images", "output", normalizedRelative);

            var url = await uploader.UploadImageAsync(localFilePath, cardId, cancellationToken);
            if (url is null)
                logger.LogWarning("Image upload failed for {Path} — replacing with placeholder", relativeImagePath);

            pathToUrl[relativeImagePath] = url;
        }

        // Second pass: replace all matches using a single Regex.Replace pass
        return LocalImageRegex.Replace(markdown, match =>
        {
            var relativeImagePath = match.Groups[2].Value;
            var altText = match.Groups[1].Value;
            var url = pathToUrl.GetValueOrDefault(relativeImagePath);

            return url is not null
                ? $"![{altText}]({url})"
                : $"[Image: {Path.GetFileName(relativeImagePath)} — upload failed]";
        });
    }
}
