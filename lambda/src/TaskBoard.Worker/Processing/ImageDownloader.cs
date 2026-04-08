using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TaskBoard.Worker.Processing;

public sealed partial class ImageDownloader(IHttpClientFactory httpClientFactory, ILogger<ImageDownloader> logger)
{
    private const string ClientName = "ImageDownloader";
    private const string ImagesRelativePath = ".aiboard/images";
    internal const int MaxImageSizeBytes = 10 * 1024 * 1024; // 10 MB
    internal const int MaxImagesPerCard = 20;

    [GeneratedRegex(@"!\[[^\]]*\]\((https?://[^\)]+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex MarkdownImageRegex();

    [GeneratedRegex(@"<img[^>]*?\bsrc=[""'](https?://[^""']+)[""'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlImageRegex();

    /// <summary>
    /// Parses all HTTP/HTTPS image URLs from markdown and HTML image references in the body.
    /// Returns unique URLs in order of first appearance.
    /// </summary>
    public static IReadOnlyList<string> ParseImageUrls(string body)
    {
        if (string.IsNullOrEmpty(body))
            return [];

        var urls = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in MarkdownImageRegex().Matches(body))
        {
            var url = m.Groups[1].Value.Trim();
            if (seen.Add(url))
                urls.Add(url);
        }

        foreach (Match m in HtmlImageRegex().Matches(body))
        {
            var url = m.Groups[1].Value.Trim();
            if (seen.Add(url))
                urls.Add(url);
        }

        return urls;
    }

    /// <summary>
    /// Downloads all images referenced in <paramref name="body"/> to
    /// <c>.aiboard/images/{cardId}/</c> under <paramref name="workspacePath"/>.
    /// Returns a mapping of original URL to workspace-relative path
    /// (e.g. <c>.aiboard/images/42/a1b2c3d4e5f6.png</c>).
    /// Download failures are logged and silently skipped — they never block agent execution.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> DownloadImagesAsync(
        string cardId, string body, string workspacePath, CancellationToken cancellationToken)
    {
        var allUrls = ParseImageUrls(body);
        if (allUrls.Count == 0)
            return new Dictionary<string, string>();

        IReadOnlyList<string> urls = allUrls;
        if (allUrls.Count > MaxImagesPerCard)
        {
            logger.LogWarning(
                "Card {CardId} references {Count} images, exceeding limit of {Max}. Only first {Max} will be downloaded.",
                cardId, allUrls.Count, MaxImagesPerCard, MaxImagesPerCard);
            urls = allUrls.Take(MaxImagesPerCard).ToList();
        }

        var imagesDir = Path.Combine(workspacePath, ImagesRelativePath, cardId);
        Directory.CreateDirectory(imagesDir);

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var client = httpClientFactory.CreateClient(ClientName);

        foreach (var url in urls)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var localRelativePath = await DownloadImageAsync(client, url, cardId, imagesDir, cancellationToken);
                if (localRelativePath is not null)
                    result[url] = localRelativePath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to download image {Url} for card {CardId}", url, cardId);
            }
        }

        logger.LogInformation("Downloaded {Count}/{Total} images for card {CardId}",
            result.Count, urls.Count, cardId);
        return result;
    }

    private async Task<string?> DownloadImageAsync(
        HttpClient client, string url, string cardId, string imagesDir, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Image download returned {StatusCode} for {Url}", (int)response.StatusCode, url);
            return null;
        }

        // Reject before downloading if Content-Length already exceeds the limit
        if (response.Content.Headers.ContentLength > MaxImageSizeBytes)
        {
            logger.LogWarning(
                "Image at {Url} exceeds {Max} bytes (Content-Length: {Size}), skipping",
                url, MaxImageSizeBytes, response.Content.Headers.ContentLength);
            return null;
        }

        var extension = GetExtension(url, response.Content.Headers.ContentType?.MediaType);
        var fileName = GetFileName(url, extension);
        var filePath = Path.Combine(imagesDir, fileName);

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var fileStream = File.Create(filePath);
        var buffer = new byte[81920];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            totalRead += bytesRead;
            if (totalRead > MaxImageSizeBytes)
            {
                logger.LogWarning(
                    "Image at {Url} exceeded {Max} bytes during streaming, skipping", url, MaxImageSizeBytes);
                fileStream.Close();
                File.Delete(filePath);
                return null;
            }
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
        }

        return $".aiboard/images/{cardId}/{fileName}";
    }

    /// <summary>
    /// Returns the deterministic filename for an image URL: SHA256 (12 hex chars) + extension.
    /// </summary>
    internal static string GetFileName(string url, string extension)
    {
        var urlBytes = Encoding.UTF8.GetBytes(url);
        var hash = SHA256.HashData(urlBytes);
        var hexPrefix = Convert.ToHexString(hash)[..12].ToLowerInvariant();
        return string.IsNullOrEmpty(extension) ? hexPrefix : $"{hexPrefix}{extension}";
    }

    /// <summary>
    /// Returns the file extension for an image, preferring the URL path, falling back to Content-Type.
    /// </summary>
    internal static string GetExtension(string url, string? contentType)
    {
        try
        {
            var uri = new Uri(url);
            var ext = Path.GetExtension(uri.AbsolutePath);
            if (!string.IsNullOrEmpty(ext) && ext.Length is >= 2 and <= 6)
                return ext.ToLowerInvariant();
        }
        catch
        {
            // Fall through to content-type
        }

        return contentType switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/png" => ".png",
            "image/gif" => ".gif",
            "image/webp" => ".webp",
            "image/svg+xml" => ".svg",
            "image/bmp" => ".bmp",
            "image/tiff" => ".tiff",
            _ => ""
        };
    }
}
