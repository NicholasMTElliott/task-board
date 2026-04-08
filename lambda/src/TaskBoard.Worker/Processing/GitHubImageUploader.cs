using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Uploads agent-generated images to a dedicated 'agent-images' branch via the GitHub Contents API.
/// Returns raw.githubusercontent.com URLs for use in GitHub issue markdown.
///
/// Images are stored at {cardId}/{hash}.{ext} on the branch, where hash is a SHA256-based
/// content hash (first 16 hex chars) for deterministic deduplication.
/// </summary>
public sealed class GitHubImageUploader(
    IOptions<GitHubProjectsOptions> options,
    ILogger<GitHubImageUploader> logger) : IImageUploader
{
    internal const string BranchName = "agent-images";
    internal const long MaxFileSizeBytes = 10 * 1024 * 1024; // 10 MB

    internal static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".bmp"
    };

    private readonly GitHubProjectsOptions _options = options.Value;

    public async Task<string?> UploadImageAsync(string localFilePath, string cardId, CancellationToken cancellationToken)
    {
        if (!File.Exists(localFilePath))
        {
            logger.LogWarning("Image file not found: {Path}", localFilePath);
            return null;
        }

        var ext = Path.GetExtension(localFilePath);
        if (!SupportedExtensions.Contains(ext))
        {
            logger.LogWarning("Unsupported image extension '{Ext}' for file: {Path}", ext, localFilePath);
            return null;
        }

        var fileInfo = new FileInfo(localFilePath);
        if (fileInfo.Length > MaxFileSizeBytes)
        {
            logger.LogWarning("Image file exceeds 10MB limit ({Size} bytes): {Path}", fileInfo.Length, localFilePath);
            return null;
        }

        var fileBytes = await File.ReadAllBytesAsync(localFilePath, cancellationToken);
        var hash = ComputeContentHash(fileBytes);
        var remotePath = $"{cardId}/{hash}{ext.ToLowerInvariant()}";
        var rawUrl = $"https://raw.githubusercontent.com/{_options.Repo}/{BranchName}/{remotePath}";

        try
        {
            await EnsureBranchExistsAsync(cancellationToken);

            if (await FileExistsOnBranchAsync(remotePath, cancellationToken))
            {
                logger.LogDebug("Image already uploaded at {Path} — reusing URL", remotePath);
                return rawUrl;
            }

            var base64Content = Convert.ToBase64String(fileBytes);
            var requestBody = JsonSerializer.Serialize(new
            {
                message = $"chore: upload image for card #{cardId}",
                content = base64Content,
                branch = BranchName
            });

            await RunGhApiAsync(
                ["api", $"repos/{_options.Repo}/contents/{remotePath}", "--method", "PUT", "--input", "-"],
                requestBody, cancellationToken);

            logger.LogInformation("Uploaded image {File} to {Url}", Path.GetFileName(localFilePath), rawUrl);
            return rawUrl;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to upload image {Path}", localFilePath);
            return null;
        }
    }

    internal static string ComputeContentHash(byte[] content)
    {
        var hash = SHA256.HashData(content);
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private async Task EnsureBranchExistsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await RunGhApiAsync(
                ["api", $"repos/{_options.Repo}/git/refs/heads/{BranchName}"],
                null, cancellationToken);
        }
        catch (InvalidOperationException ex) when (IsNotFoundError(ex))
        {
            await CreateBranchAsync(cancellationToken);
        }
    }

    private async Task CreateBranchAsync(CancellationToken cancellationToken)
    {
        // Get default branch name and HEAD SHA
        var repoJson = await RunGhApiAsync(["api", $"repos/{_options.Repo}"], null, cancellationToken);
        using var repoDoc = JsonDocument.Parse(repoJson);
        var defaultBranch = repoDoc.RootElement.GetProperty("default_branch").GetString() ?? "main";

        var refJson = await RunGhApiAsync(
            ["api", $"repos/{_options.Repo}/git/refs/heads/{defaultBranch}"],
            null, cancellationToken);
        using var refDoc = JsonDocument.Parse(refJson);
        var sha = refDoc.RootElement.GetProperty("object").GetProperty("sha").GetString()
            ?? throw new InvalidOperationException("Could not resolve default branch HEAD SHA");

        var body = JsonSerializer.Serialize(new { @ref = $"refs/heads/{BranchName}", sha });
        try
        {
            await RunGhApiAsync(
                ["api", $"repos/{_options.Repo}/git/refs", "--method", "POST", "--input", "-"],
                body, cancellationToken);
            logger.LogInformation("Created '{Branch}' branch in {Repo}", BranchName, _options.Repo);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Reference already exists", StringComparison.OrdinalIgnoreCase)
                                                   || ex.Message.Contains("HTTP 422", StringComparison.OrdinalIgnoreCase))
        {
            // Another concurrent run created the branch — that's fine
            logger.LogDebug("Branch '{Branch}' already exists (concurrent creation race)", BranchName);
        }
    }

    private async Task<bool> FileExistsOnBranchAsync(string remotePath, CancellationToken cancellationToken)
    {
        try
        {
            await RunGhApiAsync(
                ["api", $"repos/{_options.Repo}/contents/{remotePath}?ref={BranchName}"],
                null, cancellationToken);
            return true;
        }
        catch (InvalidOperationException ex) when (IsNotFoundError(ex))
        {
            return false;
        }
    }

    private async Task<string> RunGhApiAsync(string[] args, string? stdinData, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "gh",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinData is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start gh process");

        if (stdinData is not null)
        {
            await process.StandardInput.WriteAsync(stdinData);
            process.StandardInput.Close();
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"gh exited with code {process.ExitCode}. stderr: {stderr}");

        return stdout;
    }

    private static bool IsNotFoundError(InvalidOperationException ex)
        => ex.Message.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("Not Found", StringComparison.OrdinalIgnoreCase);
}
