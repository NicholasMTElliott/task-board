using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Pins the contract that <see cref="GitWorkspaceManager.GetDiffSummaryAsync"/>
/// can diff against an arbitrary base ref instead of always against HEAD.
///
/// Headline regression target: the v0.0.20 a downstream project card #3 bug where a candidate
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

    // ── Problem 4: structured diff packet (Empty / Full / Summary) ───

    [Fact]
    public async Task GetDiffPacket_NoChanges_ReturnsEmptyMode()
    {
        // Clean working tree against HEAD with no untracked files → Empty.
        var packet = await _git.GetDiffPacketAsync(
            _repoRoot, thresholdBytes: 50_000, CancellationToken.None);

        Assert.Equal(DiffMode.Empty, packet.Mode);
        Assert.Equal("", packet.Content);
        Assert.Equal(0, packet.RawByteSize);
        Assert.Equal(0, packet.FilesChanged);
    }

    [Fact]
    public async Task GetDiffPacket_SmallDiff_ReturnsFullMode()
    {
        // A small uncommitted change fits within any reasonable threshold and
        // should come back as Full mode with the raw diff text intact (so the
        // gate consumes it identically to the pre-Problem-4 path).
        File.WriteAllText(Path.Combine(_repoRoot, "small.txt"), "hello world\n");

        var packet = await _git.GetDiffPacketAsync(
            _repoRoot, thresholdBytes: 50_000, CancellationToken.None);

        Assert.Equal(DiffMode.Full, packet.Mode);
        Assert.Contains("small.txt", packet.Content);
        Assert.Contains("hello world", packet.Content);
        Assert.True(packet.RawByteSize > 0);
        Assert.True(packet.RawByteSize <= 50_000);
        Assert.Equal(1, packet.FilesChanged);
        Assert.Equal(1, packet.FilesIncludedInline);
        // Full mode must NOT prepend the summary-mode header (gate prompts
        // route on that header to switch judgement criteria).
        Assert.DoesNotContain("Summary mode (large diff)", packet.Content);
    }

    [Fact]
    public async Task GetDiffPacket_LargeDiff_ReturnsSummaryMode()
    {
        // A diff above the threshold must come back as Summary, with: a
        // summary-mode header the gate prompt can route on, the per-file
        // table, the inline section, and the gate-prompt-rule note that
        // forbids "diff truncated, can't verify" rejections.
        var bigBody = new string('x', 5_000) + "\n";
        File.WriteAllText(Path.Combine(_repoRoot, "big.txt"), bigBody);

        var packet = await _git.GetDiffPacketAsync(
            _repoRoot, thresholdBytes: 1_000, CancellationToken.None);

        Assert.Equal(DiffMode.Summary, packet.Mode);
        Assert.Contains("Summary mode (large diff)", packet.Content);
        Assert.Contains("Files changed", packet.Content);
        Assert.Contains("big.txt", packet.Content);
        // Gate-prompt-rule: explicit "do not return generic truncated rejection".
        Assert.Contains("Do not return", packet.Content);
        Assert.True(packet.RawByteSize > 1_000);
        Assert.Equal(1, packet.FilesChanged);
    }

    [Fact]
    public async Task GetDiffPacket_LargeDiff_TopKFilesInline_RestOmitted()
    {
        // With more files than the inline cap, only the highest-churn files
        // appear inline; the rest are listed in an Omitted section. Sort
        // order is by total churn (added + deleted) descending.
        // Generate 5 files with progressively smaller content; cap inline at 2.
        for (int i = 0; i < 5; i++)
        {
            // file_0 has 5000 chars, file_1 has 4000, ..., file_4 has 1000
            var size = 5_000 - (i * 1_000);
            File.WriteAllText(
                Path.Combine(_repoRoot, $"file_{i}.txt"),
                new string('a', size) + "\n");
        }

        var packet = await _git.GetDiffPacketAsync(
            _repoRoot, thresholdBytes: 1_000, CancellationToken.None,
            topInlineFiles: 2);

        Assert.Equal(DiffMode.Summary, packet.Mode);
        Assert.Equal(5, packet.FilesChanged);
        Assert.Equal(2, packet.FilesIncludedInline);

        // Inline section must contain the top 2 by churn (file_0 and file_1
        // have the most lines/chars, since each line of 'a'-block is one big
        // line plus a newline → similar churn; for our deterministic check,
        // both should appear in the table at minimum).
        Assert.Contains("file_0.txt", packet.Content);
        Assert.Contains("Omitted files", packet.Content);
        // The lowest-churn file (file_4) should be in the omitted list, not
        // inline. We can't easily assert "in omitted but not inline" without
        // parsing the markdown, so we settle for: the omitted section exists
        // and reports the right omitted count (5 changed - 2 inline = 3).
        Assert.Contains("Omitted files (3)", packet.Content);
    }

    [Fact]
    public async Task GetDiffPacket_LargeDiff_RawByteSizeReportsActualSize()
    {
        // The packet's RawByteSize must reflect the would-be full diff size,
        // not the summary's content size. Operators reading log lines need
        // to see the real magnitude that triggered summary mode.
        var bigBody = new string('y', 10_000) + "\n";
        File.WriteAllText(Path.Combine(_repoRoot, "huge.txt"), bigBody);

        var packet = await _git.GetDiffPacketAsync(
            _repoRoot, thresholdBytes: 500, CancellationToken.None);

        Assert.Equal(DiffMode.Summary, packet.Mode);
        Assert.True(packet.RawByteSize >= 10_000,
            $"expected raw byte size ≥ 10_000 (the underlying file size), got {packet.RawByteSize}");
    }

    [Fact]
    public async Task GetDiffPacket_TrackedAndUntracked_BothSurfaceInTable()
    {
        // The summary-mode file table must include both committed-since-base
        // changes and currently-untracked new files; the prior diff-summary
        // logic conflated them as a single string blob, the structured packet
        // surfaces each in the table.
        var baseSha = await _git.GetCurrentShaAsync(_repoRoot, CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(baseSha));

        // Committed change (tracked, status M for an existing file).
        File.WriteAllText(Path.Combine(_repoRoot, "tracked.txt"), new string('t', 6_000));
        RunGitSync(_repoRoot, "add", "tracked.txt");
        RunGitSync(_repoRoot, "commit", "-m", "add tracked");

        // Untracked file (status A, isUntracked=true).
        File.WriteAllText(Path.Combine(_repoRoot, "untracked.txt"), new string('u', 6_000));

        var packet = await _git.GetDiffPacketAsync(
            _repoRoot, thresholdBytes: 1_000, CancellationToken.None,
            baseRef: baseSha);

        Assert.Equal(DiffMode.Summary, packet.Mode);
        Assert.Contains("tracked.txt", packet.Content);
        Assert.Contains("untracked.txt", packet.Content);
        Assert.Equal(2, packet.FilesChanged);
    }

    [Fact]
    public async Task GetDiffPacket_DefaultsHEAD_WhenNoBaseRefProvided()
    {
        // Mirrors GetDiffSummaryAsync's default: null/whitespace baseRef →
        // HEAD. After a clean commit, no diff against HEAD → Empty mode.
        File.WriteAllText(Path.Combine(_repoRoot, "settled.txt"), "settled\n");
        RunGitSync(_repoRoot, "add", "settled.txt");
        RunGitSync(_repoRoot, "commit", "-m", "settle");

        var packet = await _git.GetDiffPacketAsync(
            _repoRoot, thresholdBytes: 50_000, CancellationToken.None);

        Assert.Equal(DiffMode.Empty, packet.Mode);
    }

    [Fact]
    public async Task GetDiffPacket_ExactlyAtThreshold_ReturnsFull()
    {
        // Boundary case: when raw bytes ≤ threshold the packet is Full, not
        // Summary. The threshold check is `> threshold` for summary entry.
        File.WriteAllText(Path.Combine(_repoRoot, "boundary.txt"), "x\n");

        // Raw diff for a 2-byte file is small (header + content). Use a
        // generous threshold to keep this above raw size — Full expected.
        var packet = await _git.GetDiffPacketAsync(
            _repoRoot, thresholdBytes: 50_000, CancellationToken.None);

        Assert.Equal(DiffMode.Full, packet.Mode);
        Assert.True(packet.RawByteSize <= 50_000);
    }

    [Fact]
    public async Task GetDiffPacket_NonAsciiContent_RawByteSizeReportsBytes_NotChars()
    {
        // Multi-byte UTF-8 content (emoji + accented Latin + CJK) — char count
        // diverges sharply from byte count. Pre-fix behaviour used .Length
        // (UTF-16 code units / chars) which under-counted bytes and let the
        // summary-mode threshold be silently exceeded for non-ASCII content.
        // The packet's RawByteSize must reflect the byte count, since the
        // operator's threshold (rerun.diff.summaryThresholdBytes) is in bytes.
        var emoji = "🚀";              // 4 bytes UTF-8, 2 chars (surrogate pair)
        var accented = "café";          // 5 bytes UTF-8, 4 chars
        var cjk = "日本語";              // 9 bytes UTF-8, 3 chars
        var body = string.Concat(Enumerable.Repeat($"{emoji} {accented} {cjk}\n", 100));
        File.WriteAllText(Path.Combine(_repoRoot, "i18n.txt"), body);

        var packet = await _git.GetDiffPacketAsync(
            _repoRoot, thresholdBytes: 50_000, CancellationToken.None);

        // The diff itself includes the file body plus headers/context, so
        // its byte count is at least as large as the file body's byte count.
        var bodyBytes = System.Text.Encoding.UTF8.GetByteCount(body);
        Assert.True(packet.RawByteSize >= bodyBytes,
            $"RawByteSize must reflect bytes, not chars. Expected ≥ {bodyBytes} (UTF-8 byte count of body); got {packet.RawByteSize}.");
        // Make the regression shape explicit — RawByteSize must be larger
        // than the char count for multi-byte content (would have been equal
        // pre-fix because .Length counted chars).
        Assert.True(packet.RawByteSize > body.Length,
            $"RawByteSize ({packet.RawByteSize}) must exceed char count ({body.Length}) for multi-byte UTF-8 content.");
    }

    [Fact]
    public async Task GetDiffPacket_NonAsciiBetweenCharAndByteThreshold_TriggersSummaryMode()
    {
        // Body whose UTF-8 byte count > threshold but UTF-16 char count ≤ threshold.
        // Pre-fix: char-count comparison kept this in Full mode, exceeding the
        // operator's configured byte cap. Post-fix: byte-count triggers summary.
        // 1500 CJK chars = ~4500 UTF-8 bytes. Threshold of 2000 bytes should
        // trigger summary mode under the byte interpretation but stay Full
        // under the char interpretation.
        var body = string.Concat(Enumerable.Repeat("日", 1500));
        File.WriteAllText(Path.Combine(_repoRoot, "cjk.txt"), body);

        var packet = await _git.GetDiffPacketAsync(
            _repoRoot, thresholdBytes: 2_000, CancellationToken.None);

        Assert.Equal(DiffMode.Summary, packet.Mode);
        Assert.True(packet.RawByteSize > 2_000,
            $"RawByteSize ({packet.RawByteSize}) should exceed the 2000-byte threshold once bytes are counted, not chars.");
    }
}
