using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Pins the contract that <see cref="GitWorkspaceManager.GetDiffSummaryAsync"/>
/// can diff against an arbitrary base ref instead of always against HEAD.
///
/// Headline regression target: the v0.0.20 KvA card #3 bug where a candidate
/// group's evaluator received empty diffs despite each candidate having
/// committed real work. Same root cause hits the gate check after a candidate
/// promotion's <c>git reset --hard</c> resets the canonical working tree to
/// HEAD. The fix is to diff against a stable pre-divergence SHA — passing
/// the captured SHA via the new <c>baseRef</c> parameter.
/// </summary>
public class GitWorkspaceManagerDiffTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly GitWorkspaceManager _git;

    public GitWorkspaceManagerDiffTests()
    {
        _repoRoot = Path.Combine(
            Path.GetTempPath(),
            "diff-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_repoRoot);
        InitGitRepo(_repoRoot);

        _git = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repoRoot, recursive: true); } catch { }
    }

    [Fact]
    public async Task GetDiffSummary_DefaultHEAD_EmptyAfterCommit()
    {
        // Baseline: after committing a change, `git diff HEAD` (the default
        // baseRef = null behaviour) returns empty because the working tree
        // matches HEAD. This is the pre-fix behaviour that misses real work
        // when the orchestrator commits before the diff is read.
        File.WriteAllText(Path.Combine(_repoRoot, "marker.txt"), "hello\n");
        RunGitSync(_repoRoot, "add", "marker.txt");
        RunGitSync(_repoRoot, "commit", "-m", "add marker");

        var diff = await _git.GetDiffSummaryAsync(_repoRoot, cancellationToken: CancellationToken.None);

        Assert.True(string.IsNullOrWhiteSpace(diff),
            $"git diff HEAD should be empty after a clean commit, got: {diff}");
    }

    [Fact]
    public async Task GetDiffSummary_ExplicitBaseRef_ShowsCommittedWorkSinceBase()
    {
        // After capturing the base SHA, committing changes, and then asking
        // for the diff against the captured base, the committed change must
        // be surfaced. This is the candidate-evaluator and gate-check fix.
        var baseSha = await _git.GetCurrentShaAsync(_repoRoot, CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(baseSha));

        File.WriteAllText(Path.Combine(_repoRoot, "added_after_base.txt"), "MARKER_LATE_ADD\n");
        RunGitSync(_repoRoot, "add", "added_after_base.txt");
        RunGitSync(_repoRoot, "commit", "-m", "add late file");

        var diff = await _git.GetDiffSummaryAsync(
            _repoRoot, cancellationToken: CancellationToken.None, baseRef: baseSha);

        Assert.Contains("added_after_base.txt", diff);
        Assert.Contains("MARKER_LATE_ADD", diff);
    }

    [Fact]
    public async Task GetDiffSummary_ExplicitBaseRef_AfterResetHard_StillShowsWork()
    {
        // Mimics the post-candidate-promotion state in the canonical worktree:
        // the orchestrator does `git reset --hard {winner-branch}`, advancing
        // canonical's HEAD to include the winner's commits AND resetting the
        // working tree so it matches HEAD. `git diff HEAD` then returns
        // empty even though real committed work is present.
        //
        // Setup mirrors that: capture base SHA, commit work, then `reset
        // --hard HEAD` to confirm the working tree is clean. The gate check's
        // `git diff <baseSha>` query must still show the work.
        var baseSha = await _git.GetCurrentShaAsync(_repoRoot, CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(baseSha));

        File.WriteAllText(Path.Combine(_repoRoot, "promoted.txt"), "PROMOTED_FROM_CANDIDATE\n");
        RunGitSync(_repoRoot, "add", "promoted.txt");
        RunGitSync(_repoRoot, "commit", "-m", "promote winner");

        // Reset --hard HEAD: working tree is now identical to HEAD (the
        // post-promotion canonical state).
        RunGitSync(_repoRoot, "reset", "--hard", "HEAD");

        // Pre-fix behaviour: `git diff HEAD` empty.
        var diffHead = await _git.GetDiffSummaryAsync(_repoRoot, cancellationToken: CancellationToken.None);
        Assert.True(string.IsNullOrWhiteSpace(diffHead),
            $"sanity: git diff HEAD should be empty post-reset, got: {diffHead}");

        // Fix: diff against the captured base SHA still surfaces committed work.
        var diffBase = await _git.GetDiffSummaryAsync(
            _repoRoot, cancellationToken: CancellationToken.None, baseRef: baseSha);
        Assert.Contains("promoted.txt", diffBase);
        Assert.Contains("PROMOTED_FROM_CANDIDATE", diffBase);
    }

    [Fact]
    public async Task GetDiffSummary_ExplicitBaseRef_IncludesUncommittedChangesToo()
    {
        // The fix must not regress the single-agent path's contract: an
        // uncommitted working-tree change must still show up. `git diff
        // <ref>` (without `..HEAD`) compares ref → working tree, so it
        // captures both committed-since-ref AND uncommitted-since-HEAD.
        var baseSha = await _git.GetCurrentShaAsync(_repoRoot, CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(baseSha));

        File.WriteAllText(Path.Combine(_repoRoot, "committed.txt"), "COMMITTED_CONTENT\n");
        RunGitSync(_repoRoot, "add", "committed.txt");
        RunGitSync(_repoRoot, "commit", "-m", "commit one");

        // Plus a still-uncommitted file (the single-agent / gate-check shape).
        File.WriteAllText(Path.Combine(_repoRoot, "uncommitted.txt"), "UNCOMMITTED_CONTENT\n");

        var diff = await _git.GetDiffSummaryAsync(
            _repoRoot, cancellationToken: CancellationToken.None, baseRef: baseSha);

        Assert.Contains("committed.txt", diff);
        Assert.Contains("COMMITTED_CONTENT", diff);
        Assert.Contains("uncommitted.txt", diff);
        Assert.Contains("UNCOMMITTED_CONTENT", diff);
    }

    [Fact]
    public async Task GetCurrentSha_ReturnsStableHexSha()
    {
        var sha = await _git.GetCurrentShaAsync(_repoRoot, CancellationToken.None);

        Assert.NotNull(sha);
        Assert.Equal(40, sha.Length);
        Assert.Matches("^[0-9a-f]{40}$", sha);
    }
}
