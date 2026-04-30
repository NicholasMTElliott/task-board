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

    private static void Run(string workspace, string provider)
        => AgentInitFileResolver.EnsureInitFile(
            workspace, provider, NullLogger.Instance);

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
}
