using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskBoard.Worker.Processing;
using TaskBoard.Worker.Tests.Helpers;

namespace TaskBoard.Worker.Tests.Processing;

public class ImageDownloaderTests : IDisposable
{
    private readonly string _tempDir;

    public ImageDownloaderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "imagedownloader-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ── ParseImageUrls ──────────────────────────────────────────────────────

    [Fact]
    public void ParseImageUrls_EmptyBody_ReturnsEmpty()
    {
        var result = ImageDownloader.ParseImageUrls("");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseImageUrls_NoImages_ReturnsEmpty()
    {
        var result = ImageDownloader.ParseImageUrls("Just plain text with no images.");
        Assert.Empty(result);
    }

    [Fact]
    public void ParseImageUrls_MarkdownImage_ReturnsUrl()
    {
        var body = "See the screenshot: ![alt text](https://example.com/image.png)";
        var result = ImageDownloader.ParseImageUrls(body);

        Assert.Single(result);
        Assert.Equal("https://example.com/image.png", result[0]);
    }

    [Fact]
    public void ParseImageUrls_HtmlImage_ReturnsUrl()
    {
        var body = @"<img src=""https://github.com/user-attachments/assets/abc123.png"" width=""800"">";
        var result = ImageDownloader.ParseImageUrls(body);

        Assert.Single(result);
        Assert.Equal("https://github.com/user-attachments/assets/abc123.png", result[0]);
    }

    [Fact]
    public void ParseImageUrls_HtmlImageSingleQuote_ReturnsUrl()
    {
        var body = "<img src='https://example.com/photo.jpg' />";
        var result = ImageDownloader.ParseImageUrls(body);

        Assert.Single(result);
        Assert.Equal("https://example.com/photo.jpg", result[0]);
    }

    [Fact]
    public void ParseImageUrls_MultipleImages_ReturnsAllUrls()
    {
        var body =
            "![screenshot](https://example.com/a.png)\n" +
            "Some text\n" +
            @"<img src=""https://example.com/b.jpg"">";

        var result = ImageDownloader.ParseImageUrls(body);

        Assert.Equal(2, result.Count);
        Assert.Equal("https://example.com/a.png", result[0]);
        Assert.Equal("https://example.com/b.jpg", result[1]);
    }

    [Fact]
    public void ParseImageUrls_DuplicateUrls_DeduplicatesResults()
    {
        var body =
            "![img](https://example.com/same.png)\n" +
            "![img again](https://example.com/same.png)";

        var result = ImageDownloader.ParseImageUrls(body);

        Assert.Single(result);
        Assert.Equal("https://example.com/same.png", result[0]);
    }

    [Fact]
    public void ParseImageUrls_DataUri_Skipped()
    {
        var body = "![data](data:image/png;base64,iVBORw0KGgo=)";
        var result = ImageDownloader.ParseImageUrls(body);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseImageUrls_RelativePath_Skipped()
    {
        var body = "![local](./images/local.png)";
        var result = ImageDownloader.ParseImageUrls(body);

        Assert.Empty(result);
    }

    [Fact]
    public void ParseImageUrls_EmptyAltText_StillParsed()
    {
        var body = "![](https://example.com/noalt.png)";
        var result = ImageDownloader.ParseImageUrls(body);

        Assert.Single(result);
        Assert.Equal("https://example.com/noalt.png", result[0]);
    }

    // ── GetFileName ─────────────────────────────────────────────────────────

    [Fact]
    public void GetFileName_ProducesHashPlusExtension()
    {
        var url = "https://example.com/image.png";
        var name = ImageDownloader.GetFileName(url, ".png");

        Assert.EndsWith(".png", name);
        Assert.Equal(16, name.Length); // 12 hex + 4 for ".png"
    }

    [Fact]
    public void GetFileName_SameUrlAlwaysProducesSameFilename()
    {
        var url = "https://example.com/stable.jpg";
        var name1 = ImageDownloader.GetFileName(url, ".jpg");
        var name2 = ImageDownloader.GetFileName(url, ".jpg");

        Assert.Equal(name1, name2);
    }

    [Fact]
    public void GetFileName_DifferentUrlsProduceDifferentFilenames()
    {
        var name1 = ImageDownloader.GetFileName("https://example.com/a.png", ".png");
        var name2 = ImageDownloader.GetFileName("https://example.com/b.png", ".png");

        Assert.NotEqual(name1, name2);
    }

    [Fact]
    public void GetFileName_NoExtension_OmitsExtension()
    {
        var name = ImageDownloader.GetFileName("https://example.com/image", "");

        Assert.Equal(12, name.Length); // only 12 hex chars
        Assert.DoesNotContain(".", name);
    }

    [Fact]
    public void GetFileName_HashPrefixMatchesSha256()
    {
        var url = "https://example.com/test.png";
        var urlBytes = Encoding.UTF8.GetBytes(url);
        var hash = SHA256.HashData(urlBytes);
        var expectedPrefix = Convert.ToHexString(hash)[..12].ToLowerInvariant();

        var name = ImageDownloader.GetFileName(url, ".png");
        Assert.StartsWith(expectedPrefix, name);
    }

    // ── GetExtension ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://example.com/photo.png", null, ".png")]
    [InlineData("https://example.com/photo.jpg", null, ".jpg")]
    [InlineData("https://example.com/photo.JPEG", null, ".jpeg")]
    [InlineData("https://example.com/photo.gif", null, ".gif")]
    [InlineData("https://example.com/photo.webp", null, ".webp")]
    public void GetExtension_FromUrl_ReturnsExtension(string url, string? contentType, string expected)
    {
        Assert.Equal(expected, ImageDownloader.GetExtension(url, contentType));
    }

    [Theory]
    [InlineData("https://github.com/user-attachments/assets/abc123", "image/png", ".png")]
    [InlineData("https://github.com/user-attachments/assets/abc123", "image/jpeg", ".jpg")]
    [InlineData("https://github.com/user-attachments/assets/abc123", "image/webp", ".webp")]
    [InlineData("https://github.com/user-attachments/assets/abc123", "image/svg+xml", ".svg")]
    [InlineData("https://github.com/user-attachments/assets/abc123", null, "")]
    public void GetExtension_NoUrlExtension_FallsBackToContentType(string url, string? contentType, string expected)
    {
        Assert.Equal(expected, ImageDownloader.GetExtension(url, contentType));
    }

    // ── DownloadImagesAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task DownloadImagesAsync_NoImages_ReturnsEmptyMapping()
    {
        var downloader = CreateDownloader(new MockHttpMessageHandler());
        var body = "A card with no image references.";

        var result = await downloader.DownloadImagesAsync("card42", body, _tempDir, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task DownloadImagesAsync_SuccessfulDownload_ReturnsRelativePath()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.EnqueueBinaryResponse(HttpStatusCode.OK, [0x89, 0x50, 0x4E, 0x47], "image/png");

        var downloader = CreateDownloader(mockHandler);
        var url = "https://example.com/screenshot.png";
        var body = $"![screenshot]({url})";

        var result = await downloader.DownloadImagesAsync("42", body, _tempDir, CancellationToken.None);

        Assert.Single(result);
        Assert.True(result.ContainsKey(url));

        var localPath = result[url];
        Assert.StartsWith(".aiboard/images/42/", localPath);
        Assert.EndsWith(".png", localPath);

        // File actually exists on disk
        var fullPath = Path.Combine(_tempDir, localPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(fullPath));
    }

    [Fact]
    public async Task DownloadImagesAsync_NotFound_SkipsAndReturnsEmpty()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.EnqueueResponse(HttpStatusCode.NotFound, "", "text/plain");

        var downloader = CreateDownloader(mockHandler);
        var body = "![missing](https://example.com/gone.png)";

        var result = await downloader.DownloadImagesAsync("42", body, _tempDir, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task DownloadImagesAsync_ContentLengthExceedsLimit_Skipped()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.EnqueueResponse(_ =>
        {
            var response = new System.Net.Http.HttpResponseMessage(HttpStatusCode.OK);
            var content = new ByteArrayContent([]);
            content.Headers.ContentLength = ImageDownloader.MaxImageSizeBytes + 1;
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            response.Content = content;
            return response;
        });

        var downloader = CreateDownloader(mockHandler);
        var body = "![big](https://example.com/huge.png)";

        var result = await downloader.DownloadImagesAsync("42", body, _tempDir, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task DownloadImagesAsync_MultipleImages_DownloadsAll()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.EnqueueBinaryResponse(HttpStatusCode.OK, [1, 2, 3], "image/png");
        mockHandler.EnqueueBinaryResponse(HttpStatusCode.OK, [4, 5, 6], "image/jpeg");

        var downloader = CreateDownloader(mockHandler);
        var body =
            "![first](https://example.com/a.png)\n" +
            "![second](https://example.com/b.jpg)";

        var result = await downloader.DownloadImagesAsync("42", body, _tempDir, CancellationToken.None);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task DownloadImagesAsync_OneFailsOneSucceeds_ReturnsSuccessfulOnly()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.EnqueueResponse(HttpStatusCode.NotFound, "", "text/plain");
        mockHandler.EnqueueBinaryResponse(HttpStatusCode.OK, [1, 2, 3], "image/png");

        var downloader = CreateDownloader(mockHandler);
        var body =
            "![missing](https://example.com/gone.png)\n" +
            "![found](https://example.com/present.png)";

        var result = await downloader.DownloadImagesAsync("42", body, _tempDir, CancellationToken.None);

        Assert.Single(result);
        Assert.True(result.ContainsKey("https://example.com/present.png"));
    }

    [Fact]
    public async Task DownloadImagesAsync_ExceedsMaxImagesPerCard_OnlyDownloadsFirst()
    {
        // Build body with MaxImagesPerCard + 1 unique image URLs
        var urls = Enumerable.Range(1, ImageDownloader.MaxImagesPerCard + 1)
            .Select(i => $"https://example.com/image{i}.png")
            .ToList();
        var body = string.Join("\n", urls.Select((u, i) => $"![img{i}]({u})"));

        var mockHandler = new MockHttpMessageHandler();
        for (var i = 0; i < ImageDownloader.MaxImagesPerCard; i++)
            mockHandler.EnqueueBinaryResponse(HttpStatusCode.OK, [1], "image/png");

        var downloader = CreateDownloader(mockHandler);

        var result = await downloader.DownloadImagesAsync("42", body, _tempDir, CancellationToken.None);

        Assert.Equal(ImageDownloader.MaxImagesPerCard, result.Count);
        // The last URL (index MaxImagesPerCard) is not in result
        Assert.False(result.ContainsKey(urls[^1]));
    }

    [Fact]
    public async Task DownloadImagesAsync_CreatesImagesDirectory()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.EnqueueBinaryResponse(HttpStatusCode.OK, [1, 2, 3], "image/png");

        var downloader = CreateDownloader(mockHandler);
        var body = "![img](https://example.com/img.png)";

        await downloader.DownloadImagesAsync("card99", body, _tempDir, CancellationToken.None);

        var expectedDir = Path.Combine(_tempDir, ".aiboard", "images", "card99");
        Assert.True(Directory.Exists(expectedDir));
    }

    [Fact]
    public async Task DownloadImagesAsync_RelativePath_ReturnsAiboardPrefix()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.EnqueueBinaryResponse(HttpStatusCode.OK, [0x47, 0x49, 0x46], "image/gif");

        var downloader = CreateDownloader(mockHandler);
        var url = "https://example.com/anim.gif";
        var body = $"![anim]({url})";

        var result = await downloader.DownloadImagesAsync("5", body, _tempDir, CancellationToken.None);

        var localPath = result[url];
        Assert.StartsWith(".aiboard/images/5/", localPath);
    }

    [Fact]
    public async Task DownloadImagesAsync_AuthHeaderOnClient_SentWithRequests()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.EnqueueBinaryResponse(HttpStatusCode.OK, [0x89, 0x50, 0x4E, 0x47], "image/png");

        var httpClient = new HttpClient(mockHandler);
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "gh_test_token");

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("ImageDownloader").Returns(httpClient);
        var downloader = new ImageDownloader(factory, NullLogger<ImageDownloader>.Instance);

        var body = "![img](https://github.com/user-attachments/assets/abc123)";
        await downloader.DownloadImagesAsync("42", body, _tempDir, CancellationToken.None);

        Assert.Single(mockHandler.SentRequests);
        var authHeader = mockHandler.SentRequests[0].Headers.Authorization;
        Assert.NotNull(authHeader);
        Assert.Equal("Bearer", authHeader.Scheme);
        Assert.Equal("gh_test_token", authHeader.Parameter);
    }

    [Fact]
    public async Task DownloadImagesAsync_UserAgentOnClient_SentWithRequests()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.EnqueueBinaryResponse(HttpStatusCode.OK, [0x89, 0x50, 0x4E, 0x47], "image/png");

        var httpClient = new HttpClient(mockHandler);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("TaskBoard-Worker/1.0");

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("ImageDownloader").Returns(httpClient);
        var downloader = new ImageDownloader(factory, NullLogger<ImageDownloader>.Instance);

        var body = "![img](https://github.com/user-attachments/assets/abc123)";
        await downloader.DownloadImagesAsync("42", body, _tempDir, CancellationToken.None);

        Assert.Single(mockHandler.SentRequests);
        var userAgent = mockHandler.SentRequests[0].Headers.UserAgent.ToString();
        Assert.Contains("TaskBoard-Worker/1.0", userAgent);
    }

    [Fact]
    public async Task DownloadImagesAsync_NoAuthHeader_RequestsSentWithoutAuth()
    {
        var mockHandler = new MockHttpMessageHandler();
        mockHandler.EnqueueBinaryResponse(HttpStatusCode.OK, [1, 2, 3], "image/png");

        var downloader = CreateDownloader(mockHandler);
        var body = "![img](https://example.com/public.png)";

        await downloader.DownloadImagesAsync("42", body, _tempDir, CancellationToken.None);

        Assert.Single(mockHandler.SentRequests);
        Assert.Null(mockHandler.SentRequests[0].Headers.Authorization);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ImageDownloader CreateDownloader(MockHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("ImageDownloader").Returns(httpClient);
        return new ImageDownloader(factory, NullLogger<ImageDownloader>.Instance);
    }
}
