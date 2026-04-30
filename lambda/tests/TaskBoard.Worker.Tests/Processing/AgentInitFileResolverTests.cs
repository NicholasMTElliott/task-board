using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Pins the cross-provider init-file mirroring behaviour of
/// <see cref="AgentInitFileResolver"/>: providers expecting the missing
/// name get a symlink (or copy fallback) from a sibling provider's file.
/// </summary>
public class AgentInitFileResolverTests : IDisposable
{
    private readonly string _tempDir;

    public AgentInitFileResolverTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(),
            "init-file-resolver-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private string Path_(string filename) => Path.Combine(_tempDir, filename);

    private void WriteFile(string filename, string content)
        => File.WriteAllText(Path_(filename), content);

    private static InitFileMirror? Run(string workspace, string provider)
        => AgentInitFileResolver.EnsureInitFile(
            workspace, provider, NullLogger.Instance);

    private static void Cleanup(InitFileMirror? mirror)
        => AgentInitFileResolver.CleanupInitFile(mirror, NullLogger.Instance);

    [Fact]
    public void ExpectedFileExists_NoOp()
    {
        WriteFile("CLAUDE.md", "claude content");
        var beforeMtime = File.GetLastWriteTimeUtc(Path_("CLAUDE.md"));

        Run(_tempDir, "claude-cli");

        // File contents and mtime unchanged.
        Assert.Equal("claude content", File.ReadAllText(Path_("CLAUDE.md")));
        Assert.Equal(beforeMtime, File.GetLastWriteTimeUtc(Path_("CLAUDE.md")));
        // No AGENTS.md created (it wasn't requested).
        Assert.False(File.Exists(Path_("AGENTS.md")));
    }

    [Fact]
    public void NeitherFileExists_NoOp()
    {
        Run(_tempDir, "claude-cli");

        Assert.False(File.Exists(Path_("CLAUDE.md")));
        Assert.False(File.Exists(Path_("AGENTS.md")));
    }

    [Fact]
    public void OnlySiblingExists_ClaudeProvider_CreatesClaudeMd()
    {
        WriteFile("AGENTS.md", "agents content");

        Run(_tempDir, "claude-cli");

        Assert.True(File.Exists(Path_("CLAUDE.md")));
        Assert.Equal("agents content", File.ReadAllText(Path_("CLAUDE.md")));
    }

    [Fact]
    public void OnlySiblingExists_OpenCodeProvider_CreatesAgentsMd()
    {
        WriteFile("CLAUDE.md", "claude content");

        Run(_tempDir, "docker-opencode");

        Assert.True(File.Exists(Path_("AGENTS.md")));
        Assert.Equal("claude content", File.ReadAllText(Path_("AGENTS.md")));
    }

    [Fact]
    public void OnlySiblingExists_CodexProvider_CreatesAgentsMd()
    {
        WriteFile("CLAUDE.md", "claude content");

        Run(_tempDir, "codex");

        Assert.True(File.Exists(Path_("AGENTS.md")));
        Assert.Equal("claude content", File.ReadAllText(Path_("AGENTS.md")));
    }

    [Theory]
    [InlineData("docker-claude-cli")]
    [InlineData("docker-claude-qwen")]
    public void OnlySiblingExists_OtherClaudeProviders_CreateClaudeMd(string provider)
    {
        WriteFile("AGENTS.md", "agents content");

        Run(_tempDir, provider);

        Assert.True(File.Exists(Path_("CLAUDE.md")));
        Assert.Equal("agents content", File.ReadAllText(Path_("CLAUDE.md")));
    }

    [Fact]
    public void StubProvider_NoOp()
    {
        WriteFile("CLAUDE.md", "claude content");

        Run(_tempDir, "stub");

        // Stub isn't in the provider map → no sibling lookup happens.
        Assert.False(File.Exists(Path_("AGENTS.md")));
    }

    [Fact]
    public void UnknownProvider_NoOp()
    {
        WriteFile("CLAUDE.md", "claude content");

        Run(_tempDir, "totally-made-up");

        Assert.False(File.Exists(Path_("AGENTS.md")));
    }

    [Fact]
    public void Idempotent_SecondCallNoOp()
    {
        WriteFile("CLAUDE.md", "claude content");

        Run(_tempDir, "docker-opencode");

        Assert.True(File.Exists(Path_("AGENTS.md")));
        var firstMtime = File.GetLastWriteTimeUtc(Path_("AGENTS.md"));

        // Wait a beat so any rewrite would be visible in mtime.
        Thread.Sleep(50);

        Run(_tempDir, "docker-opencode");

        // Second call sees the expected name already exists → no-op,
        // mtime unchanged.
        Assert.Equal(firstMtime, File.GetLastWriteTimeUtc(Path_("AGENTS.md")));
    }

    [Fact]
    public void BothFilesPresent_DistinctContent_NoOp()
    {
        WriteFile("CLAUDE.md", "claude content");
        WriteFile("AGENTS.md", "agents content (intentionally different)");

        Run(_tempDir, "docker-opencode");

        // Expected file already exists → user intent preserved.
        Assert.Equal("agents content (intentionally different)",
            File.ReadAllText(Path_("AGENTS.md")));
        Assert.Equal("claude content", File.ReadAllText(Path_("CLAUDE.md")));
    }

    [Fact]
    public void BrokenSymlink_RecoveredOnNextCall()
    {
        WriteFile("CLAUDE.md", "claude content");

        // Manually create a broken symlink at the expected name. File.Exists
        // returns false (target doesn't resolve), so the resolver's
        // existence check should pass through and clean it up.
        var brokenTarget = Path.Combine(_tempDir, "does-not-exist.md");
        try
        {
            File.CreateSymbolicLink(Path_("AGENTS.md"), brokenTarget);
        }
        catch (Exception)
        {
            // Symlink creation requires Developer Mode on Windows. If we
            // can't even create the broken-symlink fixture, the resolver
            // will fall back to the copy path on production hosts too,
            // and this scenario doesn't apply. Treat as inconclusive.
            return;
        }

        Run(_tempDir, "docker-opencode");

        Assert.True(File.Exists(Path_("AGENTS.md")));
        Assert.Equal("claude content", File.ReadAllText(Path_("AGENTS.md")));
    }

    [Fact]
    public void ResultIsReadable_ContentMatchesSource()
    {
        // The mechanism (symlink vs copy) is platform-dependent; the
        // observable contract is "file is readable, content matches".
        WriteFile("CLAUDE.md", "the quick brown fox\njumps over\n");

        Run(_tempDir, "docker-opencode");

        Assert.Equal("the quick brown fox\njumps over\n",
            File.ReadAllText(Path_("AGENTS.md")));
    }

    [Fact]
    public void CrossProviderSequence_ClaudeThenOpenCode_BothFilesEndUpCorrect()
    {
        // Realistic multi-step run: step 1 uses claude-cli (no-op since
        // CLAUDE.md is committed), step 2 uses docker-opencode (creates
        // AGENTS.md via mirror). Both files must end up readable with
        // matching content.
        WriteFile("CLAUDE.md", "project orientation");

        Run(_tempDir, "claude-cli");
        Run(_tempDir, "docker-opencode");

        Assert.True(File.Exists(Path_("CLAUDE.md")));
        Assert.True(File.Exists(Path_("AGENTS.md")));
        Assert.Equal("project orientation", File.ReadAllText(Path_("CLAUDE.md")));
        Assert.Equal("project orientation", File.ReadAllText(Path_("AGENTS.md")));
    }

    [Fact]
    public void NonExistentWorkspace_DoesNotThrow()
    {
        // Robustness: a workspace path that doesn't exist (e.g., a candidate
        // worktree torn down by a prior run, or a typo'd config) must not
        // crash the agent run. The resolver should silently no-op.
        var fakeWorkspace = Path.Combine(_tempDir, "does-not-exist", "subdir");

        var ex = Record.Exception(() => Run(fakeWorkspace, "claude-cli"));

        Assert.Null(ex);
    }

    // ── Cleanup ────────────────────────────────────────────────────────

    [Fact]
    public void EnsureInitFile_ReturnsNullWhenNothingCreated()
    {
        // Pre-existing expected file → no mirror created → returns null so
        // CleanupInitFile is a no-op for the operator's committed file.
        WriteFile("CLAUDE.md", "claude content");

        var mirror = Run(_tempDir, "claude-cli");

        Assert.Null(mirror);
    }

    [Fact]
    public void EnsureInitFile_ReturnsTokenWhenMirrorCreated()
    {
        WriteFile("CLAUDE.md", "claude content");

        var mirror = Run(_tempDir, "docker-opencode");

        Assert.NotNull(mirror);
        Assert.Equal(Path_("AGENTS.md"), mirror!.MirrorPath);
        Assert.Equal(Path_("CLAUDE.md"), mirror.SiblingPath);
    }

    [Fact]
    public void Cleanup_RemovesMirrorAfterRun()
    {
        // Headline case: mirror is created so the agent has its expected
        // init file, then removed so it doesn't get caught in a later
        // git add . && git commit and surface in the evaluator's diff.
        WriteFile("CLAUDE.md", "claude content");

        var mirror = Run(_tempDir, "docker-opencode");
        Assert.True(File.Exists(Path_("AGENTS.md")));

        Cleanup(mirror);

        Assert.False(File.Exists(Path_("AGENTS.md")));
        // Sibling untouched.
        Assert.Equal("claude content", File.ReadAllText(Path_("CLAUDE.md")));
    }

    [Fact]
    public void Cleanup_NullMirror_NoOp()
    {
        // When there was nothing to mirror (file already there, or no
        // sibling), EnsureInitFile returns null. Cleanup must safely accept
        // that without throwing or touching disk.
        WriteFile("CLAUDE.md", "claude content");
        WriteFile("AGENTS.md", "operator-managed");

        var mirror = Run(_tempDir, "docker-opencode");
        Assert.Null(mirror);

        Cleanup(mirror);

        // Both files still present — operator's intent preserved.
        Assert.Equal("claude content", File.ReadAllText(Path_("CLAUDE.md")));
        Assert.Equal("operator-managed", File.ReadAllText(Path_("AGENTS.md")));
    }

    [Fact]
    public void Cleanup_PreservesAgentEditedCopy()
    {
        // Copy fallback path: if the agent edited the mirror in place
        // (Windows non-Developer-Mode where we copied the file), cleanup
        // must NOT delete it — that would silently drop the agent's work.
        // Simulate by directly creating a file with a divergent content so
        // we don't depend on the platform's symlink permission state.
        WriteFile("CLAUDE.md", "claude content");
        WriteFile("AGENTS.md", "claude content");
        var mirror = new InitFileMirror(
            Path_("AGENTS.md"), Path_("CLAUDE.md"), InitFileMirrorKind.Copy);

        // Agent edits the mirror (e.g. the agent thought it was the source).
        File.WriteAllText(Path_("AGENTS.md"), "claude content with extra agent notes");

        Cleanup(mirror);

        // Modified mirror preserved so the operator can decide what to do.
        Assert.True(File.Exists(Path_("AGENTS.md")));
        Assert.Equal("claude content with extra agent notes",
            File.ReadAllText(Path_("AGENTS.md")));
    }

    [Fact]
    public void Cleanup_RepeatedCalls_IsIdempotent()
    {
        // A defensive caller might call cleanup more than once on the same
        // mirror token. The second call must be a silent no-op.
        WriteFile("CLAUDE.md", "claude content");
        var mirror = Run(_tempDir, "docker-opencode");

        Cleanup(mirror);
        var ex = Record.Exception(() => Cleanup(mirror));

        Assert.Null(ex);
        Assert.False(File.Exists(Path_("AGENTS.md")));
    }

    [Fact]
    public void EnsureAndCleanup_LeavesNoMirrorOnDisk_RegressionGuard()
    {
        // End-to-end: the evaluator-confusion bug was caused by the mirror
        // being left around so that the orchestrator's `git add .` captured
        // it. After EnsureInitFile + CleanupInitFile, the workspace must be
        // byte-equal to its starting state.
        WriteFile("CLAUDE.md", "project orientation");
        var beforeFiles = Directory.GetFiles(_tempDir).OrderBy(x => x).ToArray();

        var mirror = Run(_tempDir, "docker-opencode");
        Cleanup(mirror);

        var afterFiles = Directory.GetFiles(_tempDir).OrderBy(x => x).ToArray();
        Assert.Equal(beforeFiles, afterFiles);
    }
}
