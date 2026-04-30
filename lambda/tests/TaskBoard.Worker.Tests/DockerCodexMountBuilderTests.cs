using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

/// <summary>
/// Tests for <see cref="DockerCodexMountBuilder"/>. The shared worktree/<c>.git</c>
/// mount logic is exercised by <see cref="DockerClaudeMountBuilderTests"/>; this
/// suite focuses on the Codex-specific credential staging.
/// </summary>
public class DockerCodexMountBuilderTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly DockerCodexMountBuilder Builder =
        new(NullLogger<DockerCodexMountBuilder>.Instance);

    public DockerCodexMountBuilderTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "cdx-mount-builder-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task BuildAsync_NoCredPathConfiguredAndNoneDetected_OnlyWorkspaceMounts()
    {
        // Point CredentialPath at a directory that doesn't exist so the builder
        // skips the credential mount with a warning.
        var nonexistent = Path.Combine(_tempDir, "does-not-exist");
        var options = new DockerCodexAgentOptions { CredentialPath = nonexistent };

        await using var ctx = await Builder.BuildAsync(_tempDir, options);

        Assert.DoesNotContain(ctx.Mounts,
            m => m.ContainerPath.Equals(
                DockerCodexMountBuilder.DefaultCredentialMountPoint,
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildAsync_CredPathExists_StagesAndMountsAtDefaultMountPoint()
    {
        var hostCredDir = Path.Combine(_tempDir, ".codex");
        Directory.CreateDirectory(hostCredDir);
        await File.WriteAllTextAsync(
            Path.Combine(hostCredDir, "auth.json"),
            "{\"tokens\":{\"id_token\":\"fake\"}}");
        await File.WriteAllTextAsync(
            Path.Combine(hostCredDir, "config.toml"),
            "model = \"gpt-5.4-mini\"\n");

        var options = new DockerCodexAgentOptions { CredentialPath = hostCredDir };

        await using var ctx = await Builder.BuildAsync(_tempDir, options);

        var credMount = ctx.Mounts.FirstOrDefault(
            m => m.ContainerPath == DockerCodexMountBuilder.DefaultCredentialMountPoint);
        Assert.NotNull(credMount);
        Assert.False(credMount.ReadOnly,
            "Credential mount must be RW so Codex CLI can refresh tokens / write session state");
        Assert.NotEqual(hostCredDir, credMount.HostPath);
        Assert.Contains("aiboard-codex-", credMount.HostPath);

        // Staged copy contains the original files
        Assert.True(File.Exists(Path.Combine(credMount.HostPath, "auth.json")));
        Assert.True(File.Exists(Path.Combine(credMount.HostPath, "config.toml")));
    }

    [Fact]
    public async Task BuildAsync_CredPathExists_HostDirNeverMutated()
    {
        var hostCredDir = Path.Combine(_tempDir, ".codex");
        Directory.CreateDirectory(hostCredDir);
        var authPath = Path.Combine(hostCredDir, "auth.json");
        await File.WriteAllTextAsync(authPath, "{\"original\":true}");
        var originalContent = await File.ReadAllTextAsync(authPath);
        var originalMtime = File.GetLastWriteTimeUtc(authPath);

        var options = new DockerCodexAgentOptions { CredentialPath = hostCredDir };

        await using (var ctx = await Builder.BuildAsync(_tempDir, options))
        {
            // The agent inside the container would normally write to the staged
            // copy (which is RW). Verify the host's original is untouched.
            var stagedAuth = Path.Combine(
                ctx.Mounts.First(
                    m => m.ContainerPath == DockerCodexMountBuilder.DefaultCredentialMountPoint).HostPath,
                "auth.json");
            await File.WriteAllTextAsync(stagedAuth, "{\"modified\":true}");
        }

        Assert.Equal(originalContent, await File.ReadAllTextAsync(authPath));
        Assert.Equal(originalMtime, File.GetLastWriteTimeUtc(authPath));
    }

    [Fact]
    public async Task BuildAsync_CredPathExists_ExcludesHeavySubdirectories()
    {
        var hostCredDir = Path.Combine(_tempDir, ".codex");
        Directory.CreateDirectory(hostCredDir);
        await File.WriteAllTextAsync(Path.Combine(hostCredDir, "auth.json"), "{}");

        var sessionsDir = Path.Combine(hostCredDir, "sessions");
        Directory.CreateDirectory(sessionsDir);
        await File.WriteAllTextAsync(
            Path.Combine(sessionsDir, "history.jsonl"),
            "should-not-be-copied");

        var options = new DockerCodexAgentOptions { CredentialPath = hostCredDir };

        await using var ctx = await Builder.BuildAsync(_tempDir, options);

        var stagedDir = ctx.Mounts.First(
            m => m.ContainerPath == DockerCodexMountBuilder.DefaultCredentialMountPoint).HostPath;
        Assert.True(File.Exists(Path.Combine(stagedDir, "auth.json")));
        Assert.False(Directory.Exists(Path.Combine(stagedDir, "sessions")));
    }

    [Fact]
    public async Task BuildAsync_CustomCredentialMountPoint_Honoured()
    {
        var hostCredDir = Path.Combine(_tempDir, ".codex");
        Directory.CreateDirectory(hostCredDir);
        await File.WriteAllTextAsync(Path.Combine(hostCredDir, "auth.json"), "{}");

        var options = new DockerCodexAgentOptions
        {
            CredentialPath = hostCredDir,
            CredentialMountPoint = "/opt/codex-creds",
        };

        await using var ctx = await Builder.BuildAsync(_tempDir, options);

        var customMount = ctx.Mounts.FirstOrDefault(m => m.ContainerPath == "/opt/codex-creds");
        Assert.NotNull(customMount);
    }

    [Fact]
    public async Task BuildAsync_AlwaysIncludesGitOptionalLocksZero()
    {
        await using var ctx = await Builder.BuildAsync(_tempDir, new DockerCodexAgentOptions
        {
            CredentialPath = Path.Combine(_tempDir, "missing"),
        });

        Assert.True(ctx.EnvironmentVariables.TryGetValue("GIT_OPTIONAL_LOCKS", out var value));
        Assert.Equal("0", value);
    }
}
