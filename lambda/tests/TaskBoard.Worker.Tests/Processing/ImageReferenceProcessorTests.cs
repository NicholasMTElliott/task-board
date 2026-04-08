using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

public class ImageReferenceProcessorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _outputDir;
    private readonly IImageUploader _uploader;

    private const string CardId = "42";

    public ImageReferenceProcessorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "img-ref-proc-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _outputDir = Path.Combine(_tempDir, ".aiboard", "images", "output");
        Directory.CreateDirectory(_outputDir);

        _uploader = Substitute.For<IImageUploader>();
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // ── No-op cases ────────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessLocalImagesAsync_EmptyMarkdown_ReturnsUnchanged()
    {
        var result = await ImageReferenceProcessor.ProcessLocalImagesAsync(
            "", _tempDir, _uploader, CardId, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("", result);
        await _uploader.DidNotReceive().UploadImageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessLocalImagesAsync_NoImageReferences_ReturnsUnchanged()
    {
        var markdown = "# Title\n\nSome text with no images.";

        var result = await ImageReferenceProcessor.ProcessLocalImagesAsync(
            markdown, _tempDir, _uploader, CardId, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(markdown, result);
        await _uploader.DidNotReceive().UploadImageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessLocalImagesAsync_ExternalImageUrl_ReturnsUnchanged()
    {
        var markdown = "![diagram](https://example.com/diagram.png)";

        var result = await ImageReferenceProcessor.ProcessLocalImagesAsync(
            markdown, _tempDir, _uploader, CardId, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(markdown, result);
        await _uploader.DidNotReceive().UploadImageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Successful upload ──────────────────────────────────────────────────

    [Fact]
    public async Task ProcessLocalImagesAsync_LocalImagePath_UploadsAndReplacesWithUrl()
    {
        var fileName = "diagram.png";
        File.WriteAllBytes(Path.Combine(_outputDir, fileName), [1, 2, 3]);

        var hostedUrl = "https://raw.githubusercontent.com/owner/repo/agent-images/42/abc123.png";
        _uploader.UploadImageAsync(Arg.Any<string>(), CardId, Arg.Any<CancellationToken>())
            .Returns(hostedUrl);

        var markdown = $"![diagram](.aiboard/images/output/{fileName})";

        var result = await ImageReferenceProcessor.ProcessLocalImagesAsync(
            markdown, _tempDir, _uploader, CardId, NullLogger.Instance, CancellationToken.None);

        Assert.Equal($"![diagram]({hostedUrl})", result);
        await _uploader.Received(1).UploadImageAsync(
            Arg.Is<string>(p => p.EndsWith(fileName)),
            CardId,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessLocalImagesAsync_PreservesAltText()
    {
        var fileName = "screenshot.png";
        File.WriteAllBytes(Path.Combine(_outputDir, fileName), [1, 2, 3]);

        var hostedUrl = "https://raw.githubusercontent.com/owner/repo/agent-images/42/hash.png";
        _uploader.UploadImageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(hostedUrl);

        var markdown = $"![My alt text here](.aiboard/images/output/{fileName})";

        var result = await ImageReferenceProcessor.ProcessLocalImagesAsync(
            markdown, _tempDir, _uploader, CardId, NullLogger.Instance, CancellationToken.None);

        Assert.Equal($"![My alt text here]({hostedUrl})", result);
    }

    [Fact]
    public async Task ProcessLocalImagesAsync_MultipleImages_UploadsAll()
    {
        File.WriteAllBytes(Path.Combine(_outputDir, "a.png"), [1]);
        File.WriteAllBytes(Path.Combine(_outputDir, "b.png"), [2]);

        _uploader.UploadImageAsync(Arg.Is<string>(p => p.EndsWith("a.png")), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("https://example.com/a.png");
        _uploader.UploadImageAsync(Arg.Is<string>(p => p.EndsWith("b.png")), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("https://example.com/b.png");

        var markdown = "![A](.aiboard/images/output/a.png)\n![B](.aiboard/images/output/b.png)";

        var result = await ImageReferenceProcessor.ProcessLocalImagesAsync(
            markdown, _tempDir, _uploader, CardId, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("![A](https://example.com/a.png)\n![B](https://example.com/b.png)", result);
        await _uploader.Received(1).UploadImageAsync(Arg.Is<string>(p => p.EndsWith("a.png")), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _uploader.Received(1).UploadImageAsync(Arg.Is<string>(p => p.EndsWith("b.png")), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessLocalImagesAsync_SameImageReferencedTwice_UploadsOnce()
    {
        File.WriteAllBytes(Path.Combine(_outputDir, "logo.png"), [1, 2]);

        var hostedUrl = "https://example.com/logo.png";
        _uploader.UploadImageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(hostedUrl);

        var markdown = "![A](.aiboard/images/output/logo.png)\n![B](.aiboard/images/output/logo.png)";

        var result = await ImageReferenceProcessor.ProcessLocalImagesAsync(
            markdown, _tempDir, _uploader, CardId, NullLogger.Instance, CancellationToken.None);

        // Both references should be replaced with hosted URL
        Assert.Equal($"![A]({hostedUrl})\n![B]({hostedUrl})", result);
        // But upload called only once (deduplication)
        await _uploader.Received(1).UploadImageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessLocalImagesAsync_PassesCardIdToUploader()
    {
        File.WriteAllBytes(Path.Combine(_outputDir, "img.png"), [1]);
        _uploader.UploadImageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("https://example.com/img.png");

        var markdown = "![x](.aiboard/images/output/img.png)";

        await ImageReferenceProcessor.ProcessLocalImagesAsync(
            markdown, _tempDir, _uploader, "99", NullLogger.Instance, CancellationToken.None);

        await _uploader.Received(1).UploadImageAsync(Arg.Any<string>(), "99", Arg.Any<CancellationToken>());
    }

    // ── Upload failure / fallback ──────────────────────────────────────────

    [Fact]
    public async Task ProcessLocalImagesAsync_UploadFails_ReplacesWithPlaceholder()
    {
        File.WriteAllBytes(Path.Combine(_outputDir, "diagram.png"), [1]);
        _uploader.UploadImageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var markdown = "![diagram](.aiboard/images/output/diagram.png)";

        var result = await ImageReferenceProcessor.ProcessLocalImagesAsync(
            markdown, _tempDir, _uploader, CardId, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("[Image: diagram.png — upload failed]", result);
    }

    [Fact]
    public async Task ProcessLocalImagesAsync_MixedSuccessAndFailure_HandlesEachIndependently()
    {
        File.WriteAllBytes(Path.Combine(_outputDir, "ok.png"), [1]);
        File.WriteAllBytes(Path.Combine(_outputDir, "fail.png"), [2]);

        _uploader.UploadImageAsync(Arg.Is<string>(p => p.EndsWith("ok.png")), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("https://example.com/ok.png");
        _uploader.UploadImageAsync(Arg.Is<string>(p => p.EndsWith("fail.png")), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var markdown = "![ok](.aiboard/images/output/ok.png)\n![fail](.aiboard/images/output/fail.png)";

        var result = await ImageReferenceProcessor.ProcessLocalImagesAsync(
            markdown, _tempDir, _uploader, CardId, NullLogger.Instance, CancellationToken.None);

        Assert.Contains("![ok](https://example.com/ok.png)", result);
        Assert.Contains("[Image: fail.png — upload failed]", result);
    }

    // ── Path resolution ────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessLocalImagesAsync_ResolvesLocalPathRelativeToWorkspace()
    {
        var fileName = "sub.png";
        File.WriteAllBytes(Path.Combine(_outputDir, fileName), [1]);

        string? capturedPath = null;
        _uploader.UploadImageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                capturedPath = ci.ArgAt<string>(0);
                return Task.FromResult<string?>("https://example.com/sub.png");
            });

        var markdown = $"![x](.aiboard/images/output/{fileName})";

        await ImageReferenceProcessor.ProcessLocalImagesAsync(
            markdown, _tempDir, _uploader, CardId, NullLogger.Instance, CancellationToken.None);

        Assert.NotNull(capturedPath);
        Assert.True(capturedPath!.StartsWith(_tempDir), "Local path should start with workspace path");
        Assert.True(capturedPath.EndsWith(fileName), "Local path should end with the image filename");
    }

    // ── Case insensitivity ─────────────────────────────────────────────────

    [Fact]
    public async Task ProcessLocalImagesAsync_PathCaseInsensitive()
    {
        File.WriteAllBytes(Path.Combine(_outputDir, "img.PNG"), [1]);

        _uploader.UploadImageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("https://example.com/img.png");

        // Note: regex is case-insensitive on the .aiboard/images/output/ prefix
        var markdown = "![x](.AIBOARD/IMAGES/OUTPUT/img.PNG)";

        var result = await ImageReferenceProcessor.ProcessLocalImagesAsync(
            markdown, _tempDir, _uploader, CardId, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("![x](https://example.com/img.png)", result);
    }
}
