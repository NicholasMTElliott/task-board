using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Validation;

/// <summary>
/// Real probe: maps each known docker-* provider key to its configured image
/// name and runs <c>docker image inspect</c> to verify presence.
/// </summary>
public sealed class DockerImageProbe(
    IOptionsMonitor<DockerClaudeAgentOptions> claudeOpts,
    IOptionsMonitor<DockerOpenCodeAgentOptions> openCodeOpts,
    IOptionsMonitor<DockerClaudeQwenAgentOptions> claudeQwenOpts,
    IOptionsMonitor<DockerCodexAgentOptions> codexOpts) : IDockerImageProbe
{
    public async Task<IReadOnlyList<DockerImageCheck>> CheckAsync(
        IReadOnlySet<string> providerKeys, CancellationToken ct)
    {
        // provider key -> configured image name. Only docker-* providers
        // appear here; other keys (codex, claude-cli, stub) are skipped.
        var providerToImage = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["docker-claude-cli"]  = claudeOpts.CurrentValue.ImageName,
            ["docker-opencode"]    = openCodeOpts.CurrentValue.ImageName,
            ["docker-claude-qwen"] = claudeQwenOpts.CurrentValue.ImageName,
            ["docker-codex"]       = codexOpts.CurrentValue.ImageName,
        };

        var results = new List<DockerImageCheck>();
        foreach (var key in providerKeys)
        {
            if (!providerToImage.TryGetValue(key, out var image)) continue;
            results.Add(await CheckImageAsync(key, image, ct));
        }

        return results;
    }

    private static async Task<DockerImageCheck> CheckImageAsync(
        string providerKey, string image, CancellationToken ct)
    {
        try
        {
            var (exitCode, _, stderr) = await RunCommandAsync(
                "docker", ["image", "inspect", image], 5, ct);
            return new DockerImageCheck(
                providerKey, image,
                ExistsLocally: exitCode == 0,
                ProbeError: exitCode == 0 ? null : Truncate(stderr.Trim(), 240));
        }
        catch (Exception ex)
        {
            return new DockerImageCheck(
                providerKey, image,
                ExistsLocally: false,
                ProbeError: $"docker probe failed: {ex.Message}");
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCommandAsync(
        string fileName, string[] args, int timeoutSeconds, CancellationToken ct)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) process.StartInfo.ArgumentList.Add(a);

        process.Start();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            throw;
        }

        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        var stderr = await process.StandardError.ReadToEndAsync(ct);
        return (process.ExitCode, stdout, stderr);
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...[truncated]";
}

/// <summary>
/// No-op probe used when Docker isn't available. Returns an empty list so
/// validation skips the docker-image pass.
/// </summary>
public sealed class NullDockerImageProbe : IDockerImageProbe
{
    public Task<IReadOnlyList<DockerImageCheck>> CheckAsync(
        IReadOnlySet<string> providerKeys, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DockerImageCheck>>([]);
}
