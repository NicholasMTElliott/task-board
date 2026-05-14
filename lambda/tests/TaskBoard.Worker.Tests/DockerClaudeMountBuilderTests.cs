using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests;

/// <summary>
/// Unit and integration tests for <see cref="DockerClaudeMountBuilder"/> and <see cref="DockerMountContext"/>.
/// </summary>
public class DockerClaudeMountBuilderTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly DockerClaudeMountBuilder Builder =
        new(NullLogger<DockerClaudeMountBuilder>.Instance);

    public DockerClaudeMountBuilderTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "mount-builder-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── ReadWorktreeGitdirPath ───────────────────────────────────────────────

    [Fact]
    public void ReadWorktreeGitdirPath_ValidGitFile_ReturnsGitdirPath()
    {
        var worktreeDir = Path.Combine(_tempDir, "my-worktree");
        Directory.CreateDirectory(worktreeDir);
        File.WriteAllText(
            Path.Combine(worktreeDir, ".git"),
            "gitdir: /repo/.git/worktrees/my-branch\n");

        var result = DockerMountBuilderBase.ReadWorktreeGitdirPath(worktreeDir);

        Assert.Equal("/repo/.git/worktrees/my-branch", result);
    }

    [Fact]
    public void ReadWorktreeGitdirPath_CaseInsensitivePrefix_ReturnsPath()
    {
        var worktreeDir = Path.Combine(_tempDir, "case-worktree");
        Directory.CreateDirectory(worktreeDir);
        File.WriteAllText(
            Path.Combine(worktreeDir, ".git"),
            "GITDIR: /repo/.git/worktrees/branch\n");

        var result = DockerMountBuilderBase.ReadWorktreeGitdirPath(worktreeDir);

        Assert.Equal("/repo/.git/worktrees/branch", result);
    }

    [Fact]
    public void ReadWorktreeGitdirPath_GitDirectory_ReturnsNull()
    {
        // Base repos have a .git directory, not a .git file
        var repoDir = Path.Combine(_tempDir, "base-repo");
        Directory.CreateDirectory(repoDir);
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));

        var result = DockerMountBuilderBase.ReadWorktreeGitdirPath(repoDir);

        Assert.Null(result);
    }

    [Fact]
    public void ReadWorktreeGitdirPath_MissingFile_ReturnsNull()
    {
        var emptyDir = Path.Combine(_tempDir, "empty");
        Directory.CreateDirectory(emptyDir);

        var result = DockerMountBuilderBase.ReadWorktreeGitdirPath(emptyDir);

        Assert.Null(result);
    }

    [Fact]
    public void ReadWorktreeGitdirPath_NotAGitfile_ReturnsNull()
    {
        var worktreeDir = Path.Combine(_tempDir, "not-gitfile");
        Directory.CreateDirectory(worktreeDir);
        File.WriteAllText(Path.Combine(worktreeDir, ".git"), "This is not a gitfile");

        var result = DockerMountBuilderBase.ReadWorktreeGitdirPath(worktreeDir);

        Assert.Null(result);
    }

    // ── GetWorktreeName ──────────────────────────────────────────────────────

    [Fact]
    public void GetWorktreeName_UnixPath_ReturnsName()
    {
        var name = DockerMountBuilderBase.GetWorktreeName(
            "/home/user/repo/.git/worktrees/my-feature");

        Assert.Equal("my-feature", name);
    }

    [Fact]
    public void GetWorktreeName_WindowsPath_ReturnsName()
    {
        var name = DockerMountBuilderBase.GetWorktreeName(
            @"C:\Users\user\repo\.git\worktrees\feature-branch");

        Assert.Equal("feature-branch", name);
    }

    [Fact]
    public void GetWorktreeName_NoWorkteesSubdir_ReturnsNull()
    {
        var name = DockerMountBuilderBase.GetWorktreeName("/repo/.git/refs/heads/main");

        Assert.Null(name);
    }

    [Fact]
    public void GetWorktreeName_TrailingSlash_ReturnsName()
    {
        var name = DockerMountBuilderBase.GetWorktreeName(
            "/repo/.git/worktrees/my-branch/");

        Assert.Equal("my-branch", name);
    }

    // ── GetBaseGitPath ───────────────────────────────────────────────────────

    [Fact]
    public void GetBaseGitPath_UnixPath_ReturnsBaseGit()
    {
        var path = DockerMountBuilderBase.GetBaseGitPath(
            "/home/user/repo/.git/worktrees/my-feature");

        Assert.Equal("/home/user/repo/.git", path);
    }

    [Fact]
    public void GetBaseGitPath_WindowsPath_ReturnsBaseGitWithForwardSlashes()
    {
        var path = DockerMountBuilderBase.GetBaseGitPath(
            @"C:\Users\user\repo\.git\worktrees\feature-branch");

        Assert.Equal(@"C:/Users/user/repo/.git", path);
    }

    [Fact]
    public void GetBaseGitPath_NoWorkteesSubdir_ReturnsNull()
    {
        var path = DockerMountBuilderBase.GetBaseGitPath("/repo/.git/heads/main");

        Assert.Null(path);
    }

    // ── NormalizeHostPath ────────────────────────────────────────────────────

    [Fact]
    public void NormalizeHostPath_WindowsBackslashes_ConvertedToForwardSlashes()
    {
        var result = DockerMountBuilderBase.NormalizeHostPath(@"C:\Users\Nicho\repos\project");

        Assert.Equal("C:/Users/Nicho/repos/project", result);
    }

    [Fact]
    public void NormalizeHostPath_UnixPath_Unchanged()
    {
        var result = DockerMountBuilderBase.NormalizeHostPath("/home/user/repo");

        Assert.Equal("/home/user/repo", result);
    }

    [Fact]
    public void NormalizeHostPath_MixedSlashes_AllConvertedToForward()
    {
        var result = DockerMountBuilderBase.NormalizeHostPath(@"C:/Users\Nicho/repos");

        Assert.Equal("C:/Users/Nicho/repos", result);
    }

    // ── BuildAsync — workspace mount ─────────────────────────────────────────

    [Fact]
    public async Task BuildAsync_Always_IncludesWorkspaceMountAtSlashWorkspace()
    {
        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        var workspace = ctx.Mounts.FirstOrDefault(
            m => m.ContainerPath == DockerMountBuilderBase.WorkspaceMountPoint);
        Assert.NotNull(workspace);
        Assert.False(workspace.ReadOnly);
    }

    [Fact]
    public async Task BuildAsync_Always_WorkspaceMountUsesNormalizedHostPath()
    {
        // Normalize the expected path the same way the builder does
        var expected = DockerMountBuilderBase.NormalizeHostPath(_tempDir);

        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        var workspace = ctx.Mounts.First(
            m => m.ContainerPath == DockerMountBuilderBase.WorkspaceMountPoint);
        Assert.Equal(expected, workspace.HostPath);
    }

    // ── BuildAsync — git mounts ──────────────────────────────────────────────

    [Fact]
    public async Task BuildAsync_WithWorktreeGitFile_IncludesBaseGitMount()
    {
        var worktreeDir = Path.Combine(_tempDir, "worktree");
        Directory.CreateDirectory(worktreeDir);
        File.WriteAllText(
            Path.Combine(worktreeDir, ".git"),
            "gitdir: /repo/.git/worktrees/my-branch\n");

        await using var ctx = await Builder.BuildAsync(
            worktreeDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        var gitMount = ctx.Mounts.FirstOrDefault(
            m => m.ContainerPath == DockerMountBuilderBase.BaseGitMountPoint);
        Assert.NotNull(gitMount);
        Assert.True(gitMount.ReadOnly);
        Assert.Equal("/repo/.git", gitMount.HostPath);
    }

    [Fact]
    public async Task BuildAsync_WithWorktreeGitFile_IncludesGitFileOverrideMount()
    {
        var worktreeDir = Path.Combine(_tempDir, "worktree-override");
        Directory.CreateDirectory(worktreeDir);
        File.WriteAllText(
            Path.Combine(worktreeDir, ".git"),
            "gitdir: /repo/.git/worktrees/my-branch\n");

        await using var ctx = await Builder.BuildAsync(
            worktreeDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        var gitOverride = ctx.Mounts.FirstOrDefault(
            m => m.ContainerPath == $"{DockerMountBuilderBase.WorkspaceMountPoint}/.git");
        Assert.NotNull(gitOverride);
        Assert.True(gitOverride.ReadOnly);
    }

    [Fact]
    public async Task BuildAsync_WithWorktreeGitFile_GitOverrideContainsCorrectContainerGitdir()
    {
        var worktreeDir = Path.Combine(_tempDir, "worktree-content");
        Directory.CreateDirectory(worktreeDir);
        File.WriteAllText(
            Path.Combine(worktreeDir, ".git"),
            "gitdir: /repo/.git/worktrees/feature-x\n");

        await using var ctx = await Builder.BuildAsync(
            worktreeDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        // Find the temp .git override file
        var gitOverride = ctx.Mounts.First(
            m => m.ContainerPath == $"{DockerMountBuilderBase.WorkspaceMountPoint}/.git");

        // Read the content of the temp file (HostPath)
        Assert.True(File.Exists(gitOverride.HostPath),
            "Temp .git override file should exist before context is disposed");
        var content = await File.ReadAllTextAsync(gitOverride.HostPath);
        Assert.Equal(
            $"gitdir: {DockerMountBuilderBase.BaseGitMountPoint}/worktrees/feature-x",
            content);
    }

    [Fact]
    public async Task BuildAsync_WithoutGitFile_StillCreatesWorkspaceMount()
    {
        // Graceful degradation: worktree has no .git file
        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        Assert.Contains(ctx.Mounts,
            m => m.ContainerPath == DockerMountBuilderBase.WorkspaceMountPoint);
        // No base git mount or override
        Assert.DoesNotContain(ctx.Mounts,
            m => m.ContainerPath == DockerMountBuilderBase.BaseGitMountPoint);
    }

    // ── BuildAsync — credential mount ────────────────────────────────────────

    [Fact]
    public async Task BuildAsync_WithExistingCredentialPath_IncludesCredentialMount()
    {
        var credDir = Path.Combine(_tempDir, "creds");
        Directory.CreateDirectory(credDir);
        File.WriteAllText(Path.Combine(credDir, ".credentials.json"), "{\"token\":\"abc\"}");

        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = credDir },
            CancellationToken.None);

        var credMount = ctx.Mounts.FirstOrDefault(
            m => m.ContainerPath == DockerClaudeMountBuilder.DefaultCredentialMountPoint);
        Assert.NotNull(credMount);
        // Staged RW copy — the CLI needs to create session-env/ at runtime.
        Assert.False(credMount.ReadOnly);
        // Host path must NOT be the original source — it is a per-run staged copy.
        Assert.NotEqual(
            DockerMountBuilderBase.NormalizeHostPath(credDir),
            credMount.HostPath);
        Assert.Contains("aiboard-claude-", credMount.HostPath);
    }

    [Fact]
    public async Task BuildAsync_StagedCredentialCopy_ContainsSourceFiles()
    {
        var credDir = Path.Combine(_tempDir, "creds-copy");
        Directory.CreateDirectory(credDir);
        File.WriteAllText(Path.Combine(credDir, ".credentials.json"), "{\"token\":\"xyz\"}");
        File.WriteAllText(Path.Combine(credDir, "settings.json"), "{\"theme\":\"dark\"}");

        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = credDir },
            CancellationToken.None);

        var credMount = ctx.Mounts.First(
            m => m.ContainerPath == DockerClaudeMountBuilder.DefaultCredentialMountPoint);
        // HostPath uses forward slashes (Docker); convert back to native for File.Exists.
        var stagedHostPath = credMount.HostPath.Replace('/', Path.DirectorySeparatorChar);

        Assert.True(File.Exists(Path.Combine(stagedHostPath, ".credentials.json")));
        Assert.True(File.Exists(Path.Combine(stagedHostPath, "settings.json")));
        Assert.Equal("{\"token\":\"xyz\"}",
            File.ReadAllText(Path.Combine(stagedHostPath, ".credentials.json")));
    }

    [Fact]
    public async Task BuildAsync_StagedCredentialCopy_ExcludesLargeSubdirs()
    {
        var credDir = Path.Combine(_tempDir, "creds-excludes");
        Directory.CreateDirectory(credDir);
        File.WriteAllText(Path.Combine(credDir, ".credentials.json"), "{}");
        // Subdirs that should be excluded from the staged copy.
        Directory.CreateDirectory(Path.Combine(credDir, "projects"));
        File.WriteAllText(Path.Combine(credDir, "projects", "huge.log"), "noise");
        Directory.CreateDirectory(Path.Combine(credDir, "shell-snapshots"));
        File.WriteAllText(Path.Combine(credDir, "shell-snapshots", "snap.txt"), "noise");
        Directory.CreateDirectory(Path.Combine(credDir, "todos"));
        File.WriteAllText(Path.Combine(credDir, "todos", "t.md"), "noise");
        // Subdir that SHOULD be copied.
        Directory.CreateDirectory(Path.Combine(credDir, "plugins"));
        File.WriteAllText(Path.Combine(credDir, "plugins", "p.json"), "{}");

        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = credDir },
            CancellationToken.None);

        var credMount = ctx.Mounts.First(
            m => m.ContainerPath == DockerClaudeMountBuilder.DefaultCredentialMountPoint);
        var stagedHostPath = credMount.HostPath.Replace('/', Path.DirectorySeparatorChar);

        Assert.True(File.Exists(Path.Combine(stagedHostPath, ".credentials.json")));
        Assert.True(Directory.Exists(Path.Combine(stagedHostPath, "plugins")));
        Assert.False(Directory.Exists(Path.Combine(stagedHostPath, "projects")));
        Assert.False(Directory.Exists(Path.Combine(stagedHostPath, "shell-snapshots")));
        Assert.False(Directory.Exists(Path.Combine(stagedHostPath, "todos")));
    }

    [Fact]
    public async Task BuildAsync_StagedCredentialCopy_DeletedOnDispose()
    {
        var credDir = Path.Combine(_tempDir, "creds-dispose");
        Directory.CreateDirectory(credDir);
        File.WriteAllText(Path.Combine(credDir, ".credentials.json"), "{}");

        string stagedHostPath;
        var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = credDir },
            CancellationToken.None);
        try
        {
            var credMount = ctx.Mounts.First(
                m => m.ContainerPath == DockerClaudeMountBuilder.DefaultCredentialMountPoint);
            stagedHostPath = credMount.HostPath.Replace('/', Path.DirectorySeparatorChar);
            Assert.True(Directory.Exists(stagedHostPath));
        }
        finally
        {
            await ctx.DisposeAsync();
        }

        // After disposal, the staged temp directory must be gone — agent writes must
        // not leak into the user's home on subsequent runs.
        Assert.False(Directory.Exists(stagedHostPath));
        // And the host source directory must be untouched.
        Assert.True(File.Exists(Path.Combine(credDir, ".credentials.json")));
    }

    [Fact]
    public async Task BuildAsync_WithCustomCredentialMountPoint_UsesConfiguredContainerPath()
    {
        var credDir = Path.Combine(_tempDir, "creds-custom");
        Directory.CreateDirectory(credDir);

        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions
            {
                CredentialPath = credDir,
                CredentialMountPoint = "/home/custom/.claude",
            },
            CancellationToken.None);

        Assert.Contains(ctx.Mounts, m => m.ContainerPath == "/home/custom/.claude");
        Assert.DoesNotContain(ctx.Mounts,
            m => m.ContainerPath == DockerClaudeMountBuilder.DefaultCredentialMountPoint);
    }

    [Fact]
    public async Task BuildAsync_WithNonExistentCredentialPath_NoCredentialMount()
    {
        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = "/this/path/does/not/exist" },
            CancellationToken.None);

        Assert.DoesNotContain(ctx.Mounts,
            m => m.ContainerPath == DockerClaudeMountBuilder.DefaultCredentialMountPoint);
    }

    // ── BuildAsync — ~/.claude.json mount (downstream issue #9 regression guard) ───
    //
    // Claude CLI 2.x looks for $HOME/.claude.json (a FILE at the home root,
    // sibling of the .claude/ directory). When this isn't mounted into the
    // container, the CLI hard-fails with "Claude configuration file not found
    // at: /home/agent/.claude.json" before any prompt processing.
    // These tests pin the mount behaviour.

    [Fact]
    public async Task BuildAsync_WithExplicitCredentialPath_AlsoMountsClaudeJsonSibling()
    {
        var credDir = Path.Combine(_tempDir, ".claude");
        Directory.CreateDirectory(credDir);
        File.WriteAllText(Path.Combine(credDir, "settings.json"), "{}");
        // Sibling .claude.json file lives at the credential dir's parent.
        var jsonPath = Path.Combine(_tempDir, ".claude.json");
        await File.WriteAllTextAsync(jsonPath, "{\"oauthAccessToken\":\"test\"}");

        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = credDir },
            CancellationToken.None);

        var jsonMount = Assert.Single(ctx.Mounts, m => m.ContainerPath == "/home/agent/.claude.json");
        Assert.False(jsonMount.ReadOnly,
            "claude.json must be RW so the CLI's auth refresh can write to the staged copy");
        // Mount should point at a STAGED copy in temp, not the host file directly.
        Assert.NotEqual(NormalizeForwardSlashes(jsonPath), jsonMount.HostPath);
        Assert.Contains("aiboard-claude-json-", jsonMount.HostPath);
    }

    [Fact]
    public async Task BuildAsync_NoSiblingClaudeJson_DoesNotMountFile()
    {
        var credDir = Path.Combine(_tempDir, ".claude");
        Directory.CreateDirectory(credDir);
        File.WriteAllText(Path.Combine(credDir, "settings.json"), "{}");
        // Deliberately no .claude.json sibling — fresh-install host shape.

        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = credDir },
            CancellationToken.None);

        Assert.DoesNotContain(ctx.Mounts,
            m => m.ContainerPath.EndsWith(".claude.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BuildAsync_HostClaudeJsonNotMutated_StagedCopyIsTarget()
    {
        var credDir = Path.Combine(_tempDir, ".claude");
        Directory.CreateDirectory(credDir);
        var hostJsonPath = Path.Combine(_tempDir, ".claude.json");
        const string original = "{\"hostFingerprint\":\"do-not-touch\"}";
        await File.WriteAllTextAsync(hostJsonPath, original);

        string? stagedHostPath;
        await using (var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = credDir },
            CancellationToken.None))
        {
            var mount = ctx.Mounts.Single(m => m.ContainerPath == "/home/agent/.claude.json");
            stagedHostPath = mount.HostPath; // Forward-slash form — same content though.
            // Mutate the staged copy as a stand-in for the in-container CLI's auth refresh.
            // The staged path is a forward-slash version; convert back for File.* APIs.
            var diskPath = stagedHostPath.Replace('/', Path.DirectorySeparatorChar);
            await File.WriteAllTextAsync(diskPath, "{\"agentMutation\":\"ok\"}");
        }

        // Host must be untouched.
        Assert.Equal(original, await File.ReadAllTextAsync(hostJsonPath));
        // Staged temp file must be cleaned up on dispose.
        Assert.NotNull(stagedHostPath);
        var stagedDisk = stagedHostPath!.Replace('/', Path.DirectorySeparatorChar);
        Assert.False(File.Exists(stagedDisk), "staged claude.json copy should be deleted on context dispose");
    }

    [Fact]
    public async Task BuildAsync_CustomCredentialMountPoint_DerivesMatchingClaudeJsonPath()
    {
        var credDir = Path.Combine(_tempDir, ".claude");
        Directory.CreateDirectory(credDir);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, ".claude.json"), "{}");

        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions
            {
                CredentialPath = credDir,
                CredentialMountPoint = "/opt/claude-creds",
            },
            CancellationToken.None);

        // Sibling-of-dir layout preserved: dir at /opt/claude-creds → file at /opt/claude-creds.json
        Assert.Contains(ctx.Mounts, m => m.ContainerPath == "/opt/claude-creds.json");
        Assert.DoesNotContain(ctx.Mounts, m => m.ContainerPath == "/home/agent/.claude.json");
    }

    [Theory]
    [InlineData("/home/agent/.claude", "/home/agent/.claude.json")]
    [InlineData("/home/agent/.claude/", "/home/agent/.claude.json")]
    [InlineData("/opt/agent-creds", "/opt/agent-creds.json")]
    public void DeriveClaudeJsonContainerPath_StripsTrailingSlash_AppendsJson(string mount, string expected)
    {
        Assert.Equal(expected, DockerClaudeMountBuilder.DeriveClaudeJsonContainerPath(mount));
    }

    [Theory]
    [InlineData("/home/agent/.claude", "/home/agent")]
    [InlineData("/home/agent/.claude/", "/home/agent")]
    [InlineData("/claude", "/")]
    public void ParentOfCredentialMount_ReturnsHomeParent(string mount, string expected)
    {
        Assert.Equal(expected, DockerClaudeMountBuilder.ParentOfCredentialMount(mount));
    }

    private static string NormalizeForwardSlashes(string p) => p.Replace('\\', '/');

    // ── BuildAsync — environment variables ──────────────────────────────────

    [Fact]
    public async Task BuildAsync_Always_SetsGitOptionalLocksEnvVar()
    {
        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        Assert.True(ctx.EnvironmentVariables.TryGetValue("GIT_OPTIONAL_LOCKS", out var value));
        Assert.Equal("0", value);
    }

    [Fact]
    public async Task BuildAsync_SetsHomeToCredentialMountParent()
    {
        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions
            {
                CredentialPath = "nonexistent-path",
                CredentialMountPoint = "/opt/claude-creds",
            },
            CancellationToken.None);

        Assert.True(ctx.EnvironmentVariables.TryGetValue("HOME", out var home));
        Assert.Equal("/opt", home);
    }

    // ── DockerMountContext.TranslatePath ─────────────────────────────────────

    [Fact]
    public async Task TranslatePath_WorktreeSubPath_ReturnsContainerPath()
    {
        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        var subPath = Path.Combine(_tempDir, ".aiboard", "tasks", "42-feature.md");
        var result = ctx.TranslatePath(subPath);

        // Should map to /workspace/.aiboard/tasks/42-feature.md
        Assert.Equal("/workspace/.aiboard/tasks/42-feature.md", result);
    }

    [Fact]
    public async Task TranslatePath_WorktreeRoot_ReturnsWorkspaceMountPoint()
    {
        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        var result = ctx.TranslatePath(_tempDir);

        Assert.Equal(DockerMountBuilderBase.WorkspaceMountPoint, result);
    }

    [Fact]
    public async Task TranslatePath_UnmappedPath_ReturnsNull()
    {
        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        var result = ctx.TranslatePath("/some/completely/different/path/file.md");

        Assert.Null(result);
    }

    [Fact]
    public async Task TranslatePath_WindowsPathWithBackslashes_ReturnsContainerPath()
    {
        // Simulate Windows path normalization
        var windowsStyle = _tempDir.Replace('/', '\\');

        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        // TranslatePath should handle backslash normalization
        var subPath = windowsStyle + @"\.aiboard\tasks\42.md";
        var result = ctx.TranslatePath(subPath);

        Assert.Equal("/workspace/.aiboard/tasks/42.md", result);
    }

    [Fact]
    public async Task TranslatePath_PathStartingWithSamePrefix_NotMismatched()
    {
        // e.g., /tmp/mount-builder-abc and /tmp/mount-builder-abcXXX should not match
        var similar = _tempDir + "-sibling/file.md";

        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        var result = ctx.TranslatePath(similar);

        Assert.Null(result);
    }

    // ── DockerMountContext.DisposeAsync ──────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_DeletesTempGitOverrideFile()
    {
        var worktreeDir = Path.Combine(_tempDir, "worktree-dispose");
        Directory.CreateDirectory(worktreeDir);
        File.WriteAllText(
            Path.Combine(worktreeDir, ".git"),
            "gitdir: /repo/.git/worktrees/main\n");

        DockerMount? gitOverrideMount;
        var ctx = await Builder.BuildAsync(
            worktreeDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        gitOverrideMount = ctx.Mounts.FirstOrDefault(
            m => m.ContainerPath == $"{DockerMountBuilderBase.WorkspaceMountPoint}/.git");

        Assert.NotNull(gitOverrideMount);
        Assert.True(File.Exists(gitOverrideMount.HostPath), "Temp file should exist before dispose");

        await ctx.DisposeAsync();

        Assert.False(File.Exists(gitOverrideMount.HostPath), "Temp file should be deleted after dispose");
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        // First dispose
        await ctx.DisposeAsync();
        // Second dispose should be safe
        await ctx.DisposeAsync();
    }

    // ── DockerClaudeAgentExecutor.BuildDockerArgumentList with mount context ────────

    [Fact]
    public async Task BuildDockerArgumentList_WithMountContext_IncludesWorkspaceVolumeArg()
    {
        var opts = Options.Create(new DockerClaudeAgentOptions
        {
            ImageName = "aiboard-test:latest",
            PromptMountPoint = "/mnt/prompts",
        });
        var executor = new DockerClaudeAgentExecutor(opts, TaskBoard.Worker.Tests.Helpers.TestTenant.Instance, NullLogger<DockerClaudeAgentExecutor>.Instance);

        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        var args = executor.BuildDockerArgumentList("test-container", "/host/prompts", [], ctx);

        // Should contain -v {normalized_tempDir}:/workspace
        var normalizedTempDir = DockerMountBuilderBase.NormalizeHostPath(_tempDir);
        var volumeArg = $"{normalizedTempDir}:{DockerMountBuilderBase.WorkspaceMountPoint}";
        var vIdx = FindArgIndex(args, "-v");
        Assert.True(vIdx >= 0 && args.Contains(volumeArg),
            $"Expected '-v {volumeArg}' in args. Got: {string.Join(' ', args)}");
    }

    [Fact]
    public async Task BuildDockerArgumentList_WithMountContext_SetsWorkingDirectory()
    {
        var opts = Options.Create(new DockerClaudeAgentOptions { ImageName = "aiboard-test:latest" });
        var executor = new DockerClaudeAgentExecutor(opts, TaskBoard.Worker.Tests.Helpers.TestTenant.Instance, NullLogger<DockerClaudeAgentExecutor>.Instance);

        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        var args = executor.BuildDockerArgumentList("test-container", "/host/prompts", [], ctx);

        var wIdx = Array.IndexOf(args, "-w");
        Assert.True(wIdx >= 0, "Expected -w flag");
        Assert.Equal(DockerMountBuilderBase.WorkspaceMountPoint, args[wIdx + 1]);
    }

    [Fact]
    public async Task BuildDockerArgumentList_WithMountContext_InjectsGitOptionalLocksEnvVar()
    {
        var opts = Options.Create(new DockerClaudeAgentOptions { ImageName = "aiboard-test:latest" });
        var executor = new DockerClaudeAgentExecutor(opts, TaskBoard.Worker.Tests.Helpers.TestTenant.Instance, NullLogger<DockerClaudeAgentExecutor>.Instance);

        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        var args = executor.BuildDockerArgumentList("test-container", "/host/prompts", [], ctx);

        var eIdx = Array.IndexOf(args, "-e");
        Assert.True(eIdx >= 0, "Expected -e flag");
        Assert.Equal("GIT_OPTIONAL_LOCKS=0", args[eIdx + 1]);
    }

    [Fact]
    public void BuildDockerArgumentList_WithoutMountContext_NoWorkingDirectoryFlag()
    {
        var opts = Options.Create(new DockerClaudeAgentOptions { ImageName = "aiboard-test:latest" });
        var executor = new DockerClaudeAgentExecutor(opts, TaskBoard.Worker.Tests.Helpers.TestTenant.Instance, NullLogger<DockerClaudeAgentExecutor>.Instance);

        var args = executor.BuildDockerArgumentList("test-container", "/host/prompts", [], mountContext: null);

        Assert.DoesNotContain("-w", args);
    }

    // ── AgentRunner passes mounts to SessionRequest ───────────────────────────

    [Fact]
    public async Task ExecuteAsync_WithMountBuilder_SessionRequestContainsMounts()
    {
        // Arrange: set up a real git repo (AgentRunner creates a worktree)
        var baseDir = Path.Combine(_tempDir, "base-repo");
        Directory.CreateDirectory(baseDir);
        InitGitRepo(baseDir);

        SessionRequest? capturedRequest = null;
        var inner = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance);
        inner.NextOutcome = AgentOutcome.COMPLETE;
        var session = new StubSession("test-provider", inner);
        var interceptingExecutor = new CapturingSessionableExecutor(
            "test-provider", inner, session, req => capturedRequest = req);

        var config = BuildSingleStepConfig("test-provider");
        var resolver = new AgentExecutorResolver(
            new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
            {
                ["test-provider"] = interceptingExecutor,
            });

        var boardClient = Substitute.For<ITaskBoardClient>();
        const string cardId = "card-mounts-test";
        const string boardId = "board-1";
        const string listId = "list-design";

        boardClient.GetBoardCardsAsync(boardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new List<BoardCard>
            {
                new(cardId, "Test Feature", "Implement feature", listId),
            });
        boardClient.GetCardCommentsAsync(cardId, Arg.Any<CancellationToken>())
            .Returns(new List<CardComment>());

        var runner = new AgentRunner(
            boardClient,
            resolver,
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            config,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Mounts", "TestMachine"),
            new UpdateFileProcessor(boardClient, config,
                new AgentIdentity("Mounts", "TestMachine"),
                NullLogger<UpdateFileProcessor>.Instance),
            NullRunStore.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            TaskBoard.Worker.Tests.Helpers.TestTenant.Instance,
            NullLogger<AgentRunner>.Instance,
            dockerOptions: new DockerClaudeAgentOptions { ReuseContainer = true, CredentialPath = "nonexistent-path" },
            mountBuilder: Builder);

        // Act
        await runner.ExecuteAsync(cardId, boardId, baseDir, CancellationToken.None);

        // Assert: session request was made and has mounts populated
        Assert.NotNull(capturedRequest);
        Assert.NotNull(capturedRequest!.Mounts);
        Assert.NotEmpty(capturedRequest.Mounts!);

        // Workspace mount must be present
        Assert.Contains(capturedRequest.Mounts,
            m => m.ContainerPath == DockerMountBuilderBase.WorkspaceMountPoint);
    }

    // ── Integration: Docker container git ops ────────────────────────────────

    /// <summary>
    /// Verifies that git log executes successfully inside a Docker container
    /// with the worktree and base .git directory mounted.
    /// Skipped when Docker is unavailable.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task BuildAsync_Integration_GitLogWorksInMountedContainer()
    {
        if (!await IsDockerAvailableAsync())
            return;

        // Create a real git repo + worktree
        var baseDir = Path.Combine(_tempDir, "docker-integ-base");
        Directory.CreateDirectory(baseDir);
        InitGitRepo(baseDir);

        var worktreeDir = Path.Combine(_tempDir, "docker-integ-worktree");
        Directory.CreateDirectory(worktreeDir);

        // Create a worktree manually
        RunGitSync(baseDir, "worktree", "add", worktreeDir, "-b", "docker-test-branch");

        await using var ctx = await Builder.BuildAsync(
            worktreeDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        // Build docker args: mount context volumes + run git log
        var dockerArgs = BuildGitTestDockerArgs(ctx);

        var (exitCode, stdout, stderr) = await ProcessRunner.RunProcessAsync(
            "docker", dockerArgs,
            workingDirectory: worktreeDir,
            timeoutSeconds: 30,
            cancellationToken: CancellationToken.None,
            agentName: "git-log test");

        Assert.True(exitCode == 0,
            $"git log failed (exit {exitCode}). Stderr: {stderr}. Stdout: {stdout}");
        Assert.Contains("initial", stdout); // Commit message from InitGitRepo
    }

    /// <summary>
    /// Verifies that the base .git directory is mounted read-only:
    /// attempting to write to it inside the container should fail.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task BuildAsync_Integration_BaseGitMountIsReadOnly()
    {
        if (!await IsDockerAvailableAsync())
            return;

        var baseDir = Path.Combine(_tempDir, "docker-ro-base");
        Directory.CreateDirectory(baseDir);
        InitGitRepo(baseDir);

        var worktreeDir = Path.Combine(_tempDir, "docker-ro-worktree");
        Directory.CreateDirectory(worktreeDir);
        RunGitSync(baseDir, "worktree", "add", worktreeDir, "-b", "ro-test-branch");

        await using var ctx = await Builder.BuildAsync(
            worktreeDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        // Try to write to the base .git mount — should fail due to :ro
        var dockerArgs = BuildWriteToGitDockerArgs(ctx);

        var (exitCode, _, _) = await ProcessRunner.RunProcessAsync(
            "docker", dockerArgs,
            workingDirectory: worktreeDir,
            timeoutSeconds: 30,
            cancellationToken: CancellationToken.None,
            agentName: "read-only test");

        Assert.NotEqual(0, exitCode); // Write must fail
    }

    /// <summary>
    /// Verifies that files in .aiboard/ (within the worktree) are accessible
    /// inside the container since the worktree is mounted RW.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task BuildAsync_Integration_AiBoardFilesAccessibleInContainer()
    {
        if (!await IsDockerAvailableAsync())
            return;

        var baseDir = Path.Combine(_tempDir, "docker-aiboard-base");
        Directory.CreateDirectory(baseDir);
        InitGitRepo(baseDir);

        var worktreeDir = Path.Combine(_tempDir, "docker-aiboard-worktree");
        Directory.CreateDirectory(worktreeDir);
        RunGitSync(baseDir, "worktree", "add", worktreeDir, "-b", "aiboard-test-branch");

        // Write a task file into the worktree
        var aiboardDir = Path.Combine(worktreeDir, ".aiboard", "tasks");
        Directory.CreateDirectory(aiboardDir);
        File.WriteAllText(Path.Combine(aiboardDir, "42-feature.md"), "# Feature Task\n");

        await using var ctx = await Builder.BuildAsync(
            worktreeDir,
            new DockerClaudeAgentOptions { CredentialPath = "nonexistent-path" },
            CancellationToken.None);

        // Check if the file is readable inside the container
        var dockerArgs = BuildFileReadDockerArgs(ctx, "/workspace/.aiboard/tasks/42-feature.md");

        var (exitCode, stdout, stderr) = await ProcessRunner.RunProcessAsync(
            "docker", dockerArgs,
            workingDirectory: worktreeDir,
            timeoutSeconds: 30,
            cancellationToken: CancellationToken.None,
            agentName: "aiboard file test");

        Assert.True(exitCode == 0,
            $"File read failed (exit {exitCode}). Stderr: {stderr}");
        Assert.Contains("Feature Task", stdout);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string[] BuildGitTestDockerArgs(DockerMountContext ctx)
    {
        var args = new List<string> { "run", "--rm" };
        foreach (var m in ctx.Mounts)
        {
            args.Add("-v");
            var spec = $"{m.HostPath}:{m.ContainerPath}";
            if (m.ReadOnly) spec += ":ro";
            args.Add(spec);
        }
        foreach (var (k, v) in ctx.EnvironmentVariables)
        {
            args.Add("-e"); args.Add($"{k}={v}");
        }
        // -c safe.directory=* tells git to trust the host-mounted workspace
        // even when its UID doesn't match the container user (CI-only issue:
        // the GitHub runner mounts repos owned by `runner` while the alpine
        // container runs as root, triggering git's "dubious ownership" check).
        args.AddRange(["-w", DockerMountBuilderBase.WorkspaceMountPoint,
            "alpine/git", "-c", "safe.directory=*", "log", "--oneline"]);
        return args.ToArray();
    }

    private static string[] BuildWriteToGitDockerArgs(DockerMountContext ctx)
    {
        var args = new List<string> { "run", "--rm" };
        foreach (var m in ctx.Mounts)
        {
            args.Add("-v");
            var spec = $"{m.HostPath}:{m.ContainerPath}";
            if (m.ReadOnly) spec += ":ro";
            args.Add(spec);
        }
        // Attempt to write to the base .git mount (should fail — it's :ro)
        args.AddRange(["alpine", "sh", "-c", $"echo test > {DockerMountBuilderBase.BaseGitMountPoint}/test-write.txt"]);
        return args.ToArray();
    }

    private static string[] BuildFileReadDockerArgs(DockerMountContext ctx, string containerFilePath)
    {
        var args = new List<string> { "run", "--rm" };
        foreach (var m in ctx.Mounts)
        {
            args.Add("-v");
            var spec = $"{m.HostPath}:{m.ContainerPath}";
            if (m.ReadOnly) spec += ":ro";
            args.Add(spec);
        }
        args.AddRange(["alpine", "cat", containerFilePath]);
        return args.ToArray();
    }

    private static int FindArgIndex(string[] args, string arg)
        => Array.IndexOf(args, arg);

    private static async Task<bool> IsDockerAvailableAsync()
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("info");
            process.Start();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { }
                return false;
            }
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    // ── Stub helpers ─────────────────────────────────────────────────────────

    private static WorkflowConfig BuildSingleStepConfig(string provider)
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-design"] = new("Design", null, "agent_run",
                    null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-done"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("list-q"),
                        ["ERROR"] = TransitionTarget.ForColumn("list-error"),
                    },
                    GitBehavior: "discard",
                    Steps:
                    [
                        new WorkflowStep("engineer", "engineer", TaskPrompt: "Do the work"),
                    ]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["engineer"] = new("stub-model", "You are an engineer.", new List<string>(), Provider: provider),
            }).Normalised();
    }

    private sealed class StubSession : IAgentExecutorSession
    {
        private readonly StubAgentExecutor _executor;
        public string SessionId { get; } = "stub-session";
        public string ProviderKey { get; }
        public bool IsAlive { get; } = true;

        public StubSession(string providerKey, StubAgentExecutor executor)
        {
            ProviderKey = providerKey;
            _executor = executor;
        }

        public Task<AgentResult> ExecuteInSessionAsync(AgentExecutionContext ctx, CancellationToken ct)
            => _executor.ExecuteAsync(ctx, ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingSessionableExecutor(
        string providerKey,
        StubAgentExecutor inner,
        StubSession? sessionToReturn,
        Action<SessionRequest> onCreateSession) : ISessionableAgentExecutor
    {
        public string ProviderKey { get; } = providerKey;

        public Task<AgentResult> ExecuteAsync(AgentExecutionContext context, CancellationToken ct)
            => inner.ExecuteAsync(context, ct);

        public Task<IAgentExecutorSession?> TryCreateSessionAsync(
            SessionRequest request, CancellationToken ct)
        {
            onCreateSession(request);
            return Task.FromResult<IAgentExecutorSession?>(sessionToReturn);
        }
    }
}
