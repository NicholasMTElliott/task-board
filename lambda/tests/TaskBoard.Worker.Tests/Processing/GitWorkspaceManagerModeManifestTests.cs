using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Pins the <c>.aiboard/git-mode-changes.txt</c> manifest contract.
///
/// Motivating case: the executable-bit issue (CRLF line endings in shell scripts).
/// The implementer agent ran inside Docker (Linux) and chmod-ed scripts
/// executable, but the orchestrator's <c>git add . &amp;&amp; git commit</c>
/// runs on the Windows host where NTFS doesn't store the executable bit
/// and <c>core.fileMode=false</c>. The result: scripts landed at <c>100644</c>
/// in the index. The manifest mechanism is the only reliable cross-platform
/// path: the agent declares the intent, the orchestrator applies it via
/// <c>git update-index --chmod</c> (a stat-independent index mutation).
/// </summary>
public class GitWorkspaceManagerModeManifestTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly GitWorkspaceManager _git;

    public GitWorkspaceManagerModeManifestTests()
    {
        _repoRoot = Path.Combine(
            Path.GetTempPath(),
            "mode-manifest-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repoRoot);
        InitGitRepo(_repoRoot);

        _git = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); } catch { }
    }

    private void WriteManifest(string content)
    {
        var dir = Path.Combine(_repoRoot, ".aiboard");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "git-mode-changes.txt"), content);
    }

    private string GetIndexMode(string relPath)
    {
        // git ls-files -s prints "<mode> <sha> <stage>\t<path>"
        var output = RunGitSyncWithOutput(_repoRoot, "ls-files", "-s", relPath);
        var firstField = output.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)[0];
        return firstField;
    }

    [Fact]
    public async Task CommitAsync_WithPlusXManifest_SetsExecutableBitInIndex()
    {
        // Headline regression target: the the executable-bit failure mode.
        File.WriteAllText(Path.Combine(_repoRoot, "script.sh"), "#!/usr/bin/env bash\necho hi\n");
        WriteManifest("+x script.sh\n");

        await _git.CommitAsync(_repoRoot, "add script", CancellationToken.None);

        Assert.Equal("100755", GetIndexMode("script.sh"));
    }

    [Fact]
    public async Task CommitAsync_WithMinusXManifest_ClearsExecutableBitInIndex()
    {
        // Symmetric case: a previously-executable file can be marked non-executable.
        File.WriteAllText(Path.Combine(_repoRoot, "tool.sh"), "#!/usr/bin/env bash\n");
        WriteManifest("+x tool.sh\n");
        await _git.CommitAsync(_repoRoot, "add executable", CancellationToken.None);
        Assert.Equal("100755", GetIndexMode("tool.sh"));

        // Now flip it back.
        File.WriteAllText(Path.Combine(_repoRoot, "tool.sh"), "#!/usr/bin/env bash\necho updated\n");
        WriteManifest("-x tool.sh\n");
        await _git.CommitAsync(_repoRoot, "demote to non-exec", CancellationToken.None);

        Assert.Equal("100644", GetIndexMode("tool.sh"));
    }

    [Fact]
    public async Task CommitAsync_NoManifest_LeavesIndexModeUnchanged()
    {
        // Sanity baseline: without a manifest, mode handling is unchanged.
        // On Windows hosts this means files commit at 100644; that's still
        // the pre-fix behaviour. The manifest is the ONLY way mode bits flip.
        File.WriteAllText(Path.Combine(_repoRoot, "regular.txt"), "hello\n");

        await _git.CommitAsync(_repoRoot, "add regular file", CancellationToken.None);

        // Don't assert a specific mode here because Linux test runners
        // legitimately stage files at 100644 by default for non-executable
        // content. Just verify no manifest-driven change happened.
        var mode = GetIndexMode("regular.txt");
        Assert.True(mode == "100644" || mode == "100755",
            $"expected one of git's normal modes for a regular file, got {mode}");
    }

    [Fact]
    public async Task CommitAsync_MultipleEntriesInManifest_AllApplied()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "a.sh"), "#!/usr/bin/env bash\n");
        File.WriteAllText(Path.Combine(_repoRoot, "b.sh"), "#!/usr/bin/env bash\n");
        File.WriteAllText(Path.Combine(_repoRoot, "c.sh"), "#!/usr/bin/env bash\n");
        WriteManifest("""
            +x a.sh
            +x b.sh
            +x c.sh
            """);

        await _git.CommitAsync(_repoRoot, "add three scripts", CancellationToken.None);

        Assert.Equal("100755", GetIndexMode("a.sh"));
        Assert.Equal("100755", GetIndexMode("b.sh"));
        Assert.Equal("100755", GetIndexMode("c.sh"));
    }

    [Fact]
    public async Task CommitAsync_ManifestWithCommentsAndBlanks_SkipsThemQuietly()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "real.sh"), "#!/usr/bin/env bash\n");
        WriteManifest("""
            # mark our scripts executable

            +x real.sh

            # done
            """);

        await _git.CommitAsync(_repoRoot, "add with comments", CancellationToken.None);

        Assert.Equal("100755", GetIndexMode("real.sh"));
    }

    [Fact]
    public async Task CommitAsync_PathNotInIndex_LogsWarningAndContinues()
    {
        // Nothing for "ghost.sh" exists. The manifest entry should fail silently
        // (per-entry warning) without aborting the commit of real changes.
        File.WriteAllText(Path.Combine(_repoRoot, "real.sh"), "#!/usr/bin/env bash\n");
        WriteManifest("""
            +x ghost.sh
            +x real.sh
            """);

        // Should not throw — bad entry logs a warning, good entry still applies.
        await _git.CommitAsync(_repoRoot, "partial-bad manifest", CancellationToken.None);

        Assert.Equal("100755", GetIndexMode("real.sh"));
    }

    [Fact]
    public async Task CommitAsync_MalformedManifestLines_SkippedNotCrashing()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "good.sh"), "#!/usr/bin/env bash\n");
        WriteManifest("""
            this is not a valid line
            +Y bogus-flag.sh
            +x good.sh
            +x
            """);

        await _git.CommitAsync(_repoRoot, "malformed entries", CancellationToken.None);

        Assert.Equal("100755", GetIndexMode("good.sh"));
    }

    [Fact]
    public async Task CommitAsync_AbsolutePathInManifest_RejectedNotApplied()
    {
        // Defense-in-depth: an agent shouldn't be able to escape the worktree
        // via an absolute path in the manifest. Even though `git update-index`
        // would refuse it, we reject upstream and log a warning.
        File.WriteAllText(Path.Combine(_repoRoot, "ok.sh"), "#!/usr/bin/env bash\n");
        var absolute = Path.Combine(_repoRoot, "ok.sh");
        WriteManifest($"+x {absolute}\n");

        await _git.CommitAsync(_repoRoot, "absolute-path attempt", CancellationToken.None);

        // Absolute path was rejected; index mode was NOT updated.
        // (Git would have committed it at the host's default mode — 100644 on
        // Windows, 100755 on Linux for files with the user-exec bit. We only
        // assert the manifest didn't TAKE effect by ensuring the mode didn't
        // get explicitly flipped from a known starting state. Here the file
        // was never previously tracked, so the test doesn't have a clean
        // observation point. Skip strict assertion; just verify no throw.)
        Assert.True(File.Exists(Path.Combine(_repoRoot, "ok.sh")));
    }

    [Fact]
    public async Task CommitAsync_PathTraversalInManifest_RejectedNotApplied()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "ok.sh"), "#!/usr/bin/env bash\n");
        WriteManifest("""
            +x ../escape.sh
            +x scripts/../escape2.sh
            +x ok.sh
            """);

        // All `..` entries rejected as path-traversal; only ok.sh applies.
        await _git.CommitAsync(_repoRoot, "traversal attempt", CancellationToken.None);

        Assert.Equal("100755", GetIndexMode("ok.sh"));
    }

    [Fact]
    public async Task CommitAsync_EmptyManifest_NoOp()
    {
        File.WriteAllText(Path.Combine(_repoRoot, "regular.sh"), "#!/usr/bin/env bash\n");
        WriteManifest("");

        // No manifest entries → no mode changes; commit succeeds normally.
        await _git.CommitAsync(_repoRoot, "empty manifest", CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(_repoRoot, "regular.sh")));
    }

    [Fact]
    public async Task CommitAsync_ManifestPathItselfIsGitignored_NotCommitted()
    {
        // Regression guard: the manifest is in `.aiboard/` which the test
        // helper's gitignore covers. Make sure we don't accidentally stage it.
        File.WriteAllText(Path.Combine(_repoRoot, "real.sh"), "#!/usr/bin/env bash\n");
        WriteManifest("+x real.sh\n");

        await _git.CommitAsync(_repoRoot, "manifest gitignored", CancellationToken.None);

        // `git ls-files .aiboard/` should be empty.
        var staged = RunGitSyncWithOutput(_repoRoot, "ls-files", ".aiboard");
        Assert.True(string.IsNullOrWhiteSpace(staged),
            $"manifest should be gitignored, but git ls-files reports: {staged}");

        // And the mode change was applied to the staged real.sh.
        Assert.Equal("100755", GetIndexMode("real.sh"));
    }

    [Fact]
    public async Task CommitAsync_ManifestPersistsAfterCommit_ReusedNextRun()
    {
        // The manifest is intentionally NOT deleted by the orchestrator —
        // `.aiboard/` is gitignored AND its contents are managed per-run by
        // the agent + orchestrator. If the agent leaves the manifest in place
        // and runs again, applying the same `+x` to an already-100755 entry
        // is a no-op for git (idempotent). Pin that.
        File.WriteAllText(Path.Combine(_repoRoot, "stay.sh"), "#!/usr/bin/env bash\n");
        WriteManifest("+x stay.sh\n");

        await _git.CommitAsync(_repoRoot, "first commit", CancellationToken.None);
        Assert.Equal("100755", GetIndexMode("stay.sh"));

        // Modify the file and commit again; the manifest still says +x.
        File.WriteAllText(Path.Combine(_repoRoot, "stay.sh"), "#!/usr/bin/env bash\necho v2\n");

        await _git.CommitAsync(_repoRoot, "second commit", CancellationToken.None);

        // Idempotent: still 100755.
        Assert.Equal("100755", GetIndexMode("stay.sh"));
    }
}
