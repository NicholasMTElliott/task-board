using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

/// <summary>
/// Tests for <see cref="DockerCodexMountBuilder"/>. The shared worktree/<c>.git</c>
/// mount logic is exercised by <see cref="DockerClaudeMountBuilderTests"/>; this
/// suite focuses on the Codex-specific credential staging.
/// </summary>
/// <remarks>
/// As of v0.0.23 the builder produces per-file read-only mounts of each
/// top-level credential file rather than a single dir-level RW mount of the
/// staged copy. The change avoids a Docker Desktop Windows + WSL2 bind-mount
/// permissions issue where the in-container <c>agent</c> user could not
/// <c>mkdir sessions/</c> inside a Windows-temp-backed dir mount. The image
/// pre-creates the agent-owned <c>/home/agent/.codex/</c> directory, and per-file
/// mounts overlay credential files onto it without changing its permissions.
/// </remarks>
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

    private static IEnumerable<DockerMount> CredentialFileMounts(DockerMountContext ctx) =>
        ctx.Mounts.Where(m => m.ContainerPath.StartsWith(
            DockerCodexMountBuilder.DefaultCredentialMountPoint + "/", StringComparison.Ordinal));

    [Fact]
    public async Task BuildAsync_NoCredPathConfiguredAndNoneDetected_OnlyWorkspaceMounts()
    {
        // Point CredentialPath at a directory that doesn't exist so the builder
        // skips the credential mount with a warning.
        var nonexistent = Path.Combine(_tempDir, "does-not-exist");
        var options = new DockerCodexAgentOptions { CredentialPath = nonexistent };

        await using var ctx = await Builder.BuildAsync(_tempDir, options);

        Assert.DoesNotContain(ctx.Mounts,
            m => m.ContainerPath.StartsWith(
                DockerCodexMountBuilder.DefaultCredentialMountPoint, StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_CredPathExists_CreatesPerFileRoMounts_NotDirectoryRwMount()
    {
        // The headline regression guard: a single dir-level mount at
        // /home/agent/.codex (RW) was the v0.0.22 KvA failure shape — Codex
        // CLI could not mkdir sessions/ inside it on Docker Desktop Windows.
        // The fix is per-file RO mounts.
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

        // No mount at the bare /home/agent/.codex (would re-introduce the bug).
        Assert.DoesNotContain(ctx.Mounts,
            m => m.ContainerPath == DockerCodexMountBuilder.DefaultCredentialMountPoint);

        var credMounts = CredentialFileMounts(ctx).ToList();
        Assert.Equal(2, credMounts.Count);
        Assert.All(credMounts, m => Assert.True(
            m.ReadOnly, "Per-file credential mounts must be RO."));
    }

    [Fact]
    public async Task BuildAsync_CredPathExists_FilesUnderCredentialMountPoint()
    {
        var hostCredDir = Path.Combine(_tempDir, ".codex");
        Directory.CreateDirectory(hostCredDir);
        await File.WriteAllTextAsync(Path.Combine(hostCredDir, "auth.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(hostCredDir, "config.toml"), "");

        var options = new DockerCodexAgentOptions { CredentialPath = hostCredDir };

        await using var ctx = await Builder.BuildAsync(_tempDir, options);

        var paths = CredentialFileMounts(ctx).Select(m => m.ContainerPath).ToHashSet();
        Assert.Contains($"{DockerCodexMountBuilder.DefaultCredentialMountPoint}/auth.json", paths);
        Assert.Contains($"{DockerCodexMountBuilder.DefaultCredentialMountPoint}/config.toml", paths);
    }

    [Fact]
    public async Task BuildAsync_CredPathExists_StagedCopyExistsOnDisk()
    {
        // Per-file mounts still use a per-run staged copy under %TEMP%.
        // The HostPath of each credential mount points into that staged dir,
        // and the file content is the operator's original.
        var hostCredDir = Path.Combine(_tempDir, ".codex");
        Directory.CreateDirectory(hostCredDir);
        await File.WriteAllTextAsync(Path.Combine(hostCredDir, "auth.json"),
            "{\"tokens\":{\"id_token\":\"original\"}}");

        var options = new DockerCodexAgentOptions { CredentialPath = hostCredDir };

        await using var ctx = await Builder.BuildAsync(_tempDir, options);

        var authMount = CredentialFileMounts(ctx).Single(m => m.ContainerPath.EndsWith("/auth.json"));
        Assert.True(File.Exists(authMount.HostPath));
        Assert.NotEqual(Path.Combine(hostCredDir, "auth.json"), authMount.HostPath);
        Assert.Contains("aiboard-codex-", authMount.HostPath);
        Assert.Equal(
            "{\"tokens\":{\"id_token\":\"original\"}}",
            await File.ReadAllTextAsync(authMount.HostPath));
    }

    [Fact]
    public async Task BuildAsync_CredPathExists_HostDirNeverMutated_RegressionGuard()
    {
        // Even though credentials are now mounted RO into the container, the
        // host's ~/.codex/ must remain untouched by aiboard's staging — only
        // the per-run temp dir is touched. This test simulates writing to the
        // staged copy (still possible host-side, since file system perms
        // aren't enforced from the host) and verifies the original is intact.
        var hostCredDir = Path.Combine(_tempDir, ".codex");
        Directory.CreateDirectory(hostCredDir);
        var authPath = Path.Combine(hostCredDir, "auth.json");
        await File.WriteAllTextAsync(authPath, "{\"original\":true}");
        var originalContent = await File.ReadAllTextAsync(authPath);
        var originalMtime = File.GetLastWriteTimeUtc(authPath);

        var options = new DockerCodexAgentOptions { CredentialPath = hostCredDir };

        await using (var ctx = await Builder.BuildAsync(_tempDir, options))
        {
            var stagedAuth = CredentialFileMounts(ctx)
                .Single(m => m.ContainerPath.EndsWith("/auth.json"))
                .HostPath;
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

        var stagedDir = Path.GetDirectoryName(
            CredentialFileMounts(ctx).First().HostPath)!;
        Assert.True(File.Exists(Path.Combine(stagedDir, "auth.json")));
        Assert.False(Directory.Exists(Path.Combine(stagedDir, "sessions")));
    }

    [Fact]
    public async Task BuildAsync_NoSubdirMounts_RegressionGuard()
    {
        // Even if a non-excluded subdirectory survives the copy (e.g. a future
        // ".codex/profiles" the operator created), it must NOT be mounted.
        // Mounting a subdir would re-introduce the dir-level perms issue this
        // design specifically avoids.
        var hostCredDir = Path.Combine(_tempDir, ".codex");
        Directory.CreateDirectory(hostCredDir);
        await File.WriteAllTextAsync(Path.Combine(hostCredDir, "auth.json"), "{}");

        // A subdir not in the exclude list — gets copied to the staged dir but
        // must NOT be mounted.
        var profilesDir = Path.Combine(hostCredDir, "profiles");
        Directory.CreateDirectory(profilesDir);
        await File.WriteAllTextAsync(Path.Combine(profilesDir, "default.toml"), "");

        var options = new DockerCodexAgentOptions { CredentialPath = hostCredDir };

        await using var ctx = await Builder.BuildAsync(_tempDir, options);

        var paths = CredentialFileMounts(ctx).Select(m => m.ContainerPath).ToList();
        Assert.Single(paths,
            p => p == $"{DockerCodexMountBuilder.DefaultCredentialMountPoint}/auth.json");
        Assert.DoesNotContain(paths,
            p => p.Contains("/profiles", StringComparison.Ordinal));
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

        var customMount = ctx.Mounts.FirstOrDefault(m => m.ContainerPath == "/opt/codex-creds/auth.json");
        Assert.NotNull(customMount);
        Assert.True(customMount.ReadOnly);
    }

    [Fact]
    public async Task BuildAsync_StagedDirHasNoTopLevelFiles_NoCredentialMountsButTempDirStillCleanedUp()
    {
        // Edge case: operator's ~/.codex/ contains only an excluded subdirectory
        // (e.g. just sessions/), no top-level files. After exclusion-aware copy
        // the staged dir ends up empty. The builder must NOT add mounts pointing
        // at non-existent files, AND must still register the staged dir for
        // cleanup so we don't leak temp dirs across runs.
        var hostCredDir = Path.Combine(_tempDir, ".codex");
        Directory.CreateDirectory(hostCredDir);
        // Only an excluded subdir; nothing at the top level.
        Directory.CreateDirectory(Path.Combine(hostCredDir, "sessions"));
        await File.WriteAllTextAsync(
            Path.Combine(hostCredDir, "sessions", "history.jsonl"), "noise");

        var options = new DockerCodexAgentOptions { CredentialPath = hostCredDir };

        // Capture the staged dir's path before disposal so we can assert it was deleted.
        string? stagedDirPath;
        await using (var ctx = await Builder.BuildAsync(_tempDir, options))
        {
            Assert.Empty(CredentialFileMounts(ctx));

            // Reach into the temp-dir tracker indirectly: the builder always names
            // staged dirs `aiboard-codex-{guid}` under Path.GetTempPath(). Even
            // when no file mounts exist, the staged dir was created (and registered
            // for cleanup) so we can find it by enumerating temp.
            stagedDirPath = Directory
                .EnumerateDirectories(Path.GetTempPath(), "aiboard-codex-*")
                .OrderByDescending(d => Directory.GetCreationTimeUtc(d))
                .FirstOrDefault();
            Assert.NotNull(stagedDirPath);
            Assert.True(Directory.Exists(stagedDirPath));
        }

        // After disposal, the staged dir must be gone — pin the cleanup path.
        Assert.False(Directory.Exists(stagedDirPath),
            "Staged credential dir must be cleaned up on DockerMountContext disposal " +
            "even when it produced zero file mounts.");
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
