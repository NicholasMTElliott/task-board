using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests;

public class GitWorkspaceManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _worktreeBase;
    private readonly GitWorkspaceManager _manager;
    private readonly string _defaultBranch;

    public GitWorkspaceManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "gitwm-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _worktreeBase = _tempDir + "-worktrees";
        Directory.CreateDirectory(_tempDir);
        _manager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);

        // Initialize a git repo in the temp dir
        InitGitRepo(_tempDir);
        _defaultBranch = RunGitSyncWithOutput(_tempDir, "branch", "--show-current");
    }

    public void Dispose()
    {
        CleanupDirectory(_worktreeBase);

        // Prune worktrees before deleting the main repo (avoids locked refs)
        try { RunGitSync(_tempDir, "worktree", "prune"); } catch { }

        CleanupDirectory(_tempDir);
    }

    // ── Worktree tests ────────────────────────────────────────────────

    [Fact]
    public async Task CreateWorktreeAsync_CreatesWorktreeAndBranch()
    {
        var path = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/test-card", CancellationToken.None);

        Assert.True(Directory.Exists(path), $"Worktree directory should exist at {path}");

        // Branch inside the worktree should be correct
        var branch = await _manager.GetCurrentBranchAsync(path, CancellationToken.None);
        Assert.Equal("aiboard/test-card", branch);

        // Main repo should still be on its original branch
        var mainBranch = await _manager.GetCurrentBranchAsync(_tempDir, CancellationToken.None);
        Assert.Equal(_defaultBranch, mainBranch);
    }

    [Fact]
    public async Task CreateWorktreeAsync_BranchAlreadyExists_AttachesToExistingBranch()
    {
        // Create the branch manually first
        RunGitSync(_tempDir, "branch", "aiboard/existing-card");

        var path = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/existing-card", CancellationToken.None);

        Assert.True(Directory.Exists(path));
        var branch = await _manager.GetCurrentBranchAsync(path, CancellationToken.None);
        Assert.Equal("aiboard/existing-card", branch);
    }

    [Fact]
    public async Task CreateWorktreeAsync_WorktreeAlreadyExists_ReusesIt()
    {
        var path1 = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/reuse-card", CancellationToken.None);
        var path2 = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/reuse-card", CancellationToken.None);

        Assert.Equal(path1, path2);
        Assert.True(Directory.Exists(path2));
    }

    [Fact]
    public async Task CreateWorktreeAsync_StaleDirectory_CleansUpAndCreates()
    {
        var worktreePath = Path.GetFullPath(GitWorkspaceManager.GetWorktreePath(_tempDir, "aiboard/stale-card"));

        // Create a stale directory (not a registered worktree)
        Directory.CreateDirectory(worktreePath);
        File.WriteAllText(Path.Combine(worktreePath, "junk.txt"), "stale");

        var path = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/stale-card", CancellationToken.None);

        Assert.True(Directory.Exists(path));
        var branch = await _manager.GetCurrentBranchAsync(path, CancellationToken.None);
        Assert.Equal("aiboard/stale-card", branch);
    }

    [Fact]
    public async Task RemoveWorktreeAsync_RemovesWorktreeDirectory()
    {
        var path = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/remove-card", CancellationToken.None);
        Assert.True(Directory.Exists(path));

        await _manager.RemoveWorktreeAsync(_tempDir, "aiboard/remove-card", deleteBranch: false, CancellationToken.None);

        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task RemoveWorktreeAsync_WithDeleteBranch_DeletesBranch()
    {
        await _manager.CreateWorktreeAsync(_tempDir, "aiboard/delete-branch-card", CancellationToken.None);

        await _manager.RemoveWorktreeAsync(_tempDir, "aiboard/delete-branch-card", deleteBranch: true, CancellationToken.None);

        var branchExists = await _manager.BranchExistsAsync(_tempDir, "aiboard/delete-branch-card", CancellationToken.None);
        Assert.False(branchExists);
    }

    [Fact]
    public async Task RemoveWorktreeAsync_NonExistent_DoesNotThrow()
    {
        // Should not throw even if worktree doesn't exist
        await _manager.RemoveWorktreeAsync(_tempDir, "aiboard/never-created", deleteBranch: false, CancellationToken.None);
    }

    [Fact]
    public async Task RemoveWorktreeAsync_NonExistentWithDeleteBranch_DoesNotThrow()
    {
        // Cleanup paths in CandidateExecutor pass deleteBranch: true even when
        // the candidate-creation step failed (no worktree, no branch). Must
        // still not throw — the cleanup loop relies on this.
        await _manager.RemoveWorktreeAsync(
            _tempDir, "aiboard-cand/1-deadbeef-0-docker-claude-cli",
            deleteBranch: true, CancellationToken.None);
    }

    [Fact]
    public async Task RemoveWorktreeAsync_DirectoryExistsButNotRegistered_StillCleansUp()
    {
        // Simulates a partially-cleaned-up state from a prior crash: directory
        // is on disk but git has no worktree registration for it. The git
        // worktree remove will fail (unknown working tree) but the fallback
        // Directory.Delete + worktree prune should still clean up.
        var orphanPath = Path.Combine(_worktreeBase, "aiboard-cand", "orphan-leftover");
        Directory.CreateDirectory(orphanPath);
        File.WriteAllText(Path.Combine(orphanPath, "leftover.txt"), "stale");
        Assert.True(Directory.Exists(orphanPath));

        await _manager.RemoveWorktreeAsync(
            _tempDir, "aiboard-cand/orphan-leftover",
            deleteBranch: true, CancellationToken.None);

        Assert.False(Directory.Exists(orphanPath));
    }

    [Fact]
    public async Task RemoveWorktreeAsync_HandDeletedDirectory_NoStaleRegistration()
    {
        // Sanity check for the partially-cleaned state where a worktree's
        // directory was hand-deleted (or removed by another process) without
        // git's involvement. The cleanup chain — git worktree remove (may
        // fail), Directory.Delete fallback (no-op), git worktree prune —
        // should leave no leftover entry in `git worktree list`. We don't
        // pin which step does the work; only the post-condition matters.
        var path = await _manager.CreateWorktreeAsync(
            _tempDir, "aiboard-cand/prune-test", CancellationToken.None);
        Directory.Delete(path, recursive: true);

        await _manager.RemoveWorktreeAsync(
            _tempDir, "aiboard-cand/prune-test",
            deleteBranch: true, CancellationToken.None);

        var registrations = RunGitSyncWithOutput(_tempDir, "worktree", "list", "--porcelain");
        Assert.DoesNotContain("aiboard-cand/prune-test", registrations);
    }

    [Fact]
    public async Task RemoveWorktreeAsync_NeitherWorktreeNorBranchExist_DoesNotThrow()
    {
        // Independent-step regression guard: even when EVERY git sub-step
        // fails (no worktree to remove, no directory to delete, no branch to
        // delete), the method must complete cleanly. This is what the
        // candidate-cleanup loop relies on — a single bad teardown can't
        // stop the loop from completing the remaining candidates.
        await _manager.RemoveWorktreeAsync(
            _tempDir, "aiboard-cand/no-such-branch-here",
            deleteBranch: true, CancellationToken.None);

        // No side-effect assertion: the only requirement is that it didn't
        // throw. The other tests pin the success-path side effects.
    }

    // ── Commit tests (inside worktree) ────────────────────────────────

    [Fact]
    public async Task CommitAsync_CommitsNonIgnoredFiles()
    {
        var worktreePath = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/commit-card", CancellationToken.None);

        // Create a non-ignored file
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "design.md"), "# Technical Design");

        await _manager.CommitAsync(worktreePath, "Agent: design for card1", CancellationToken.None);

        var (_, logOutput, _) = await GitWorkspaceManager.RunGitAsync(
            worktreePath, ["log", "--oneline", "-1"], CancellationToken.None);
        Assert.Contains("Agent: design for card1", logOutput);
    }

    [Fact]
    public async Task CommitAsync_IgnoresAiboardFiles_NoOp()
    {
        var worktreePath = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/ignored-card", CancellationToken.None);

        // Create only .aiboard/ files (gitignored) — commit should be a no-op
        var tasksDir = Path.Combine(worktreePath, ".aiboard", "tasks");
        Directory.CreateDirectory(tasksDir);
        await File.WriteAllTextAsync(Path.Combine(tasksDir, "card1.md"), "test content");

        // Should not throw — just skips the commit
        await _manager.CommitAsync(worktreePath, "Should be skipped", CancellationToken.None);

        // Verify no commit was created (still on initial commit)
        var (_, logOutput, _) = await GitWorkspaceManager.RunGitAsync(
            worktreePath, ["log", "--oneline"], CancellationToken.None);
        Assert.DoesNotContain("Should be skipped", logOutput);
    }

    [Fact]
    public async Task CommitAsync_NoChanges_IsNoOp()
    {
        var worktreePath = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/empty-commit", CancellationToken.None);

        // Should not throw — just skips the commit
        await _manager.CommitAsync(worktreePath, "Empty commit", CancellationToken.None);

        var (_, logOutput, _) = await GitWorkspaceManager.RunGitAsync(
            worktreePath, ["log", "--oneline"], CancellationToken.None);
        Assert.DoesNotContain("Empty commit", logOutput);
    }

    // ── Helper tests ──────────────────────────────────────────────────

    [Fact]
    public async Task GetCurrentBranchAsync_ReturnsMainOnInit()
    {
        var branch = await _manager.GetCurrentBranchAsync(_tempDir, CancellationToken.None);

        Assert.Equal(_defaultBranch, branch);
    }

    [Fact]
    public async Task BranchExistsAsync_ReturnsFalseForNonExistent()
    {
        var exists = await _manager.BranchExistsAsync(_tempDir, "aiboard/no-such-branch", CancellationToken.None);
        Assert.False(exists);
    }

    [Fact]
    public async Task BranchExistsAsync_ReturnsTrueForExisting()
    {
        RunGitSync(_tempDir, "branch", "aiboard/exists");
        var exists = await _manager.BranchExistsAsync(_tempDir, "aiboard/exists", CancellationToken.None);
        Assert.True(exists);
    }

    [Fact]
    public async Task GetWorktreePath_ProducesExpectedPath()
    {
        var path = GitWorkspaceManager.GetWorktreePath("/repo/root", "aiboard/card-123");
        var expected = Path.Combine("/repo/root-worktrees", "aiboard", "card-123");
        Assert.Equal(expected, path);
    }

    [Fact]
    public void ResolveWorktreePath_UsesConfiguredBase()
    {
        var managerWithBase = new GitWorkspaceManager(
            NullLogger<GitWorkspaceManager>.Instance,
            worktreeBasePath: "/custom/worktrees");

        var path = managerWithBase.ResolveWorktreePath("/repo/root", "aiboard/card-123");
        var expected = Path.Combine("/custom/worktrees", "aiboard", "card-123");
        Assert.Equal(expected, path);
    }

    [Fact]
    public void ResolveWorktreePath_FallsBackToDefault_WhenNoBaseConfigured()
    {
        // _manager was created without a worktreeBasePath
        var path = _manager.ResolveWorktreePath("/repo/root", "aiboard/card-123");
        var expected = Path.Combine("/repo/root-worktrees", "aiboard", "card-123");
        Assert.Equal(expected, path);
    }

    [Fact]
    public async Task HasStagedChangesAsync_NoChanges_ReturnsFalse()
    {
        var result = await _manager.HasStagedChangesAsync(_tempDir, CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task HasStagedChangesAsync_WithStagedFiles_ReturnsTrue()
    {
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "staged.txt"), "content");
        await GitWorkspaceManager.RunGitAsync(_tempDir, ["add", "staged.txt"], CancellationToken.None);

        var result = await _manager.HasStagedChangesAsync(_tempDir, CancellationToken.None);
        Assert.True(result);
    }

    [Fact]
    public async Task RunGitAsync_InvalidCommand_ThrowsGitOperationException()
    {
        var ex = await Assert.ThrowsAsync<GitOperationException>(
            () => GitWorkspaceManager.RunGitAsync(
                _tempDir, ["not-a-real-command"], CancellationToken.None));

        Assert.NotEqual(0, ex.ExitCode);
    }

    // ── GetDiffSummaryAsync tests ────────────────────────────────────

    [Fact]
    public async Task GetDiffSummaryAsync_ModifiedTrackedFile_IncludesDiff()
    {
        var worktreePath = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/diff-modified", CancellationToken.None);

        // Modify an existing tracked file
        await File.WriteAllTextAsync(Path.Combine(worktreePath, ".gitkeep"), "modified content");

        var diff = await _manager.GetDiffSummaryAsync(worktreePath, cancellationToken: CancellationToken.None);

        Assert.Contains(".gitkeep", diff);
        Assert.Contains("modified content", diff);
    }

    [Fact]
    public async Task GetDiffSummaryAsync_NewUntrackedFile_IncludesFileContent()
    {
        var worktreePath = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/diff-untracked", CancellationToken.None);

        // Create a new untracked file
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "new-feature.cs"),
            "public class NewFeature { }");

        var diff = await _manager.GetDiffSummaryAsync(worktreePath, cancellationToken: CancellationToken.None);

        Assert.Contains("new-feature.cs", diff);
        Assert.Contains("NewFeature", diff);
    }

    [Fact]
    public async Task GetDiffSummaryAsync_NoChanges_ReturnsEmpty()
    {
        var worktreePath = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/diff-empty", CancellationToken.None);

        var diff = await _manager.GetDiffSummaryAsync(worktreePath, cancellationToken: CancellationToken.None);

        Assert.True(string.IsNullOrWhiteSpace(diff));
    }

    [Fact]
    public async Task GetDiffSummaryAsync_ExceedsMaxChars_Truncates()
    {
        var worktreePath = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/diff-truncate", CancellationToken.None);

        // Create a file that will produce a diff larger than our small limit
        var largeContent = new string('x', 500);
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "large.txt"), largeContent);

        var diff = await _manager.GetDiffSummaryAsync(worktreePath, maxChars: 100, cancellationToken: CancellationToken.None);

        Assert.Contains("diff truncated", diff);
    }

    [Fact]
    public async Task GetDiffSummaryAsync_BothModifiedAndUntracked_IncludesBoth()
    {
        var worktreePath = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/diff-both", CancellationToken.None);

        // Modify tracked file
        await File.WriteAllTextAsync(Path.Combine(worktreePath, ".gitkeep"), "changed");
        // Add new untracked file
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "added.txt"), "new file");

        var diff = await _manager.GetDiffSummaryAsync(worktreePath, cancellationToken: CancellationToken.None);

        Assert.Contains(".gitkeep", diff);
        Assert.Contains("added.txt", diff);
        Assert.Contains("new file", diff);
    }

    // ── Git read command tests ────────────────────────────────────────
    // Verify that read-only git commands work inside a worktree.
    // The orchestrator-owns-git-writes principle requires that agents
    // can still read history/state but must not run write commands.

    [Fact]
    public async Task RunGitAsync_LogInWorktree_ReturnsCommitHistory()
    {
        var worktreePath = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/log-test", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "feature.cs"), "public class Feature { }");
        await _manager.CommitAsync(worktreePath, "feat: add feature for log test", CancellationToken.None);

        var (exitCode, stdout, _) = await GitWorkspaceManager.RunGitAsync(
            worktreePath, ["log", "--oneline", "-1"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("feat: add feature for log test", stdout);
    }

    [Fact]
    public async Task RunGitAsync_StatusInWorktree_ShowsModifiedFiles()
    {
        var worktreePath = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/status-test", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "modified.cs"), "// uncommitted change");

        var (exitCode, stdout, _) = await GitWorkspaceManager.RunGitAsync(
            worktreePath, ["status", "--short"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("modified.cs", stdout);
    }

    [Fact]
    public async Task RunGitAsync_DiffInWorktree_ShowsChanges()
    {
        var worktreePath = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/diff-read-test", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(worktreePath, ".gitkeep"), "new content here");

        var (exitCode, stdout, _) = await GitWorkspaceManager.RunGitAsync(
            worktreePath, ["diff"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("new content here", stdout);
    }

    [Fact]
    public async Task RunGitAsync_ShowInWorktree_ReturnsCommitDetails()
    {
        var worktreePath = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/show-test", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "show-me.cs"), "public class ShowMe { }");
        await _manager.CommitAsync(worktreePath, "feat: commit for show test", CancellationToken.None);

        var (exitCode, stdout, _) = await GitWorkspaceManager.RunGitAsync(
            worktreePath, ["show", "--stat", "HEAD"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("show-me.cs", stdout);
    }

    [Fact]
    public async Task RunGitAsync_BlameInWorktree_ReturnsLineAnnotations()
    {
        var worktreePath = await _manager.CreateWorktreeAsync(_tempDir, "aiboard/blame-test", CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(worktreePath, "blame-me.cs"), "line one\nline two\n");
        await _manager.CommitAsync(worktreePath, "feat: commit for blame test", CancellationToken.None);

        var (exitCode, stdout, _) = await GitWorkspaceManager.RunGitAsync(
            worktreePath, ["blame", "blame-me.cs"], CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains("line one", stdout);
        Assert.Contains("line two", stdout);
    }

    // ── Merge step tests (Approach D: gate diff base advances on merge) ──

    [Fact]
    public async Task MergeMainBranchAsync_BranchBehindMainline_ReportsMergedMainSha()
    {
        // origin (bare) ← _tempDir publishes the default branch
        var originDir = Path.Combine(_worktreeBase, "origin-behind.git");
        Directory.CreateDirectory(originDir);
        RunGitSync(originDir, "init", "--bare");
        RunGitSync(_tempDir, "remote", "add", "origin", originDir);
        RunGitSync(_tempDir, "push", "origin", _defaultBranch);

        // Card branch off the current default-branch tip, with its own commit
        RunGitSync(_tempDir, "checkout", "-b", "aiboard/card");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "card-work.txt"), "card change\n");
        RunGitSync(_tempDir, "add", ".");
        RunGitSync(_tempDir, "commit", "-m", "card work");

        // Mainline moves forward; the card branch fetches the new tip
        RunGitSync(_tempDir, "checkout", _defaultBranch);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "mainline-change.txt"), "mainline change\n");
        RunGitSync(_tempDir, "add", ".");
        RunGitSync(_tempDir, "commit", "-m", "mainline work");
        RunGitSync(_tempDir, "push", "origin", _defaultBranch);
        var mainlineSha = RunGitSyncWithOutput(_tempDir, "rev-parse", $"origin/{_defaultBranch}");
        RunGitSync(_tempDir, "checkout", "aiboard/card");
        RunGitSync(_tempDir, "fetch", "origin");

        var result = await _manager.MergeMainBranchAsync(_tempDir, _defaultBranch, CancellationToken.None);

        Assert.NotEqual(MergeMainStatus.UpToDate, result.Status);
        // The merged mainline SHA is the new merge-base — and therefore the
        // gate-check diff base under Approach D.
        Assert.Equal(mainlineSha, result.MergedMainSha);

        // Leave the temp repo clean for Dispose (the merge is --no-commit).
        try { RunGitSync(_tempDir, "merge", "--abort"); } catch { /* nothing to abort */ }
    }

    [Fact]
    public async Task MergeMainBranchAsync_BranchUpToDate_StillReportsMergedMainSha()
    {
        var originDir = Path.Combine(_worktreeBase, "origin-uptodate.git");
        Directory.CreateDirectory(originDir);
        RunGitSync(originDir, "init", "--bare");
        RunGitSync(_tempDir, "remote", "add", "origin", originDir);
        RunGitSync(_tempDir, "push", "origin", _defaultBranch);

        RunGitSync(_tempDir, "checkout", "-b", "aiboard/uptodate");
        RunGitSync(_tempDir, "fetch", "origin");
        var mainlineSha = RunGitSyncWithOutput(_tempDir, "rev-parse", $"origin/{_defaultBranch}");

        var result = await _manager.MergeMainBranchAsync(_tempDir, _defaultBranch, CancellationToken.None);

        Assert.Equal(MergeMainStatus.UpToDate, result.Status);
        // Even with nothing to merge, origin/{default} IS the merge-base, so a
        // re-run can advance a stale gate diff base to it.
        Assert.Equal(mainlineSha, result.MergedMainSha);
    }

    // ── Test infrastructure ───────────────────────────────────────────

    private static void InitGitRepo(string path)
    {
        RunGitSync(path, "init");
        RunGitSync(path, "config", "user.email", "test@test.com");
        RunGitSync(path, "config", "user.name", "Test");
        // Create initial commit with .gitignore matching the real repo
        File.WriteAllText(Path.Combine(path, ".gitignore"), ".aiboard/\n");
        File.WriteAllText(Path.Combine(path, ".gitkeep"), "");
        RunGitSync(path, "add", ".");
        RunGitSync(path, "commit", "-m", "initial");
    }

    private static void CleanupDirectory(string path)
    {
        if (!Directory.Exists(path))
            return;

        // Git pack files are read-only on Windows — clear attributes before deleting
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }

        Directory.Delete(path, recursive: true);
    }
}
