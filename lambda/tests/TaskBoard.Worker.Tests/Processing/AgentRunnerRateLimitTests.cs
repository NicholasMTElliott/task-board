using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Tests for AgentRunner's rate-limit catch-restore-rethrow behavior.
/// </summary>
public class AgentRunnerRateLimitTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _worktreeBase;
    private readonly ITaskBoardClient _boardClient;
    private readonly IAgentExecutor _throwingExecutor;

    private const string TriggerColumn = "Ready for Design";
    private const string CardId = "card-42";
    private const string CardTitle = "Auth Feature";
    private const string BoardId = "board-1";

    public AgentRunnerRateLimitTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "rl-tests-" + Guid.NewGuid().ToString("N")[..8]);
        _worktreeBase = _tempDir + "-worktrees";
        Directory.CreateDirectory(_tempDir);
        InitTestGitRepo(_tempDir);

        _boardClient = Substitute.For<ITaskBoardClient>();
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(new List<BoardCard>
            {
                new(CardId, CardTitle, "Description", TriggerColumn),
            });
        _boardClient.GetCardCommentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CardComment>>(new List<CardComment>()));

        _throwingExecutor = Substitute.For<IAgentExecutor>();
        _throwingExecutor.ExecuteAsync(Arg.Any<AgentExecutionContext>(), Arg.Any<CancellationToken>())
            .Throws(new RateLimitException("Claude CLI rate limited", RateLimitSource.AgentCli));
    }

    public void Dispose()
    {
        CleanupDirectory(_worktreeBase);
        try { RunGitSync(_tempDir, "worktree", "prune"); } catch { }
        CleanupDirectory(_tempDir);
    }

    private static void CleanupDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(path, recursive: true);
    }

    private static void InitTestGitRepo(string path)
    {
        RunGitSync(path, "init");
        RunGitSync(path, "config", "user.email", "test@test.com");
        RunGitSync(path, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(path, ".gitignore"), ".aiboard/\n");
        File.WriteAllText(Path.Combine(path, ".gitkeep"), "");
        RunGitSync(path, "add", ".");
        RunGitSync(path, "commit", "-m", "initial");
    }

    private AgentRunner CreateRunner(string gitBehavior = "discard", IRunStore? runStore = null)
    {
        var config = BuildWorkflowConfig(gitBehavior).Normalised();
        return new AgentRunner(
            _boardClient,
            AgentExecutorResolver.ForSingleExecutor(_throwingExecutor),
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            config,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Agent", "TestMachine"),
            new UpdateFileProcessor(_boardClient, config, new AgentIdentity("Agent", "TestMachine"), NullLogger<UpdateFileProcessor>.Instance),
            runStore ?? NullRunStore.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            TaskBoard.Worker.Tests.Helpers.TestTenant.Instance,
            NullLogger<AgentRunner>.Instance);
    }

    private static WorkflowConfig BuildWorkflowConfig(string gitBehavior = "discard")
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [TriggerColumn] = new("Ready for Design", "senior_engineer", "agent_run",
                    "Design task {TaskName} ({TaskId})",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["IN_PROGRESS"] = TransitionTarget.ForColumn("Designing"),
                        ["COMPLETE"] = TransitionTarget.ForColumn("Designed"),
                        ["NEEDS_INFO"] = TransitionTarget.ForColumn("Design Questions"),
                        ["ERROR"] = TransitionTarget.ForColumn("Error"),
                    },
                    GitBehavior: gitBehavior),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new("claude-opus-4-6", "You are a Senior Engineer.",
                    new List<string> { "Technical Design" }),
            });
    }

    [Fact]
    public async Task ExecuteAsync_RateLimit_RethrowsRateLimitException()
    {
        var runner = CreateRunner();

        await Assert.ThrowsAsync<RateLimitException>(
            () => runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_RateLimit_RestoresCardToTriggerColumn()
    {
        var runner = CreateRunner();

        await Assert.ThrowsAsync<RateLimitException>(
            () => runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None));

        await _boardClient.Received(1).MoveCardToColumnAsync(
            CardId, TriggerColumn, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RateLimit_PostsInformationalComment()
    {
        var runner = CreateRunner();

        await Assert.ThrowsAsync<RateLimitException>(
            () => runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None));

        await _boardClient.Received(1).UpsertAgentCommentAsync(
            CardId,
            Arg.Is<string>(s => s.Contains("Rate limited")),
            Arg.Is<string>(m => m.Contains("agent-rate-limit:")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RateLimit_CommentMarkerContainsCardId()
    {
        var runner = CreateRunner();

        await Assert.ThrowsAsync<RateLimitException>(
            () => runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None));

        await _boardClient.Received(1).UpsertAgentCommentAsync(
            CardId,
            Arg.Any<string>(),
            Arg.Is<string>(m => m == $"<!-- agent-rate-limit:{CardId} -->"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RateLimit_DoesNotMoveToErrorColumn()
    {
        var runner = CreateRunner();

        await Assert.ThrowsAsync<RateLimitException>(
            () => runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None));

        // Error column should NOT be called
        await _boardClient.DidNotReceive().MoveCardToColumnAsync(
            CardId, "Error", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RateLimit_CardRestorationFails_StillRethrows()
    {
        var runner = CreateRunner();

        // Make the board client throw on restore
        _boardClient.MoveCardToColumnAsync(CardId, TriggerColumn, Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("board down")));

        // Still rethrows the rate limit exception (not the board failure)
        await Assert.ThrowsAsync<RateLimitException>(
            () => runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_RateLimit_DiscardStage_WorktreeIsCleanedUp()
    {
        var runner = CreateRunner(gitBehavior: "discard");

        await Assert.ThrowsAsync<RateLimitException>(
            () => runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None));

        // After cleanup, the leaf worktree directory should not exist.
        // The parent aiboard/ directory may remain (git worktree remove only removes the leaf).
        var worktreesBase = _tempDir + "-worktrees";
        var aiboardDir = Path.Combine(worktreesBase, "aiboard");
        var hasLeafWorktree = Directory.Exists(aiboardDir)
            && Directory.EnumerateDirectories(aiboardDir).Any();
        Assert.False(hasLeafWorktree,
            "Leaf worktree directory should have been cleaned up but child directories remain under aiboard/");
    }

    [Fact]
    public async Task ExecuteAsync_RateLimit_IncrementsRateLimitEventsCounter()
    {
        // V22: per-run counter on agent_run is bumped every time AgentRunner
        // catches a RateLimitException, so operators can spot capacity-contention
        // patterns without grepping logs.
        var recording = new CountingRunStore();
        var runner = CreateRunner(runStore: recording);

        await Assert.ThrowsAsync<RateLimitException>(
            () => runner.ExecuteAsync(CardId, BoardId, _tempDir, CancellationToken.None));

        Assert.True(recording.RateLimitIncrements >= 1,
            $"Expected IncrementRateLimitEventsAsync to be called at least once, got {recording.RateLimitIncrements}");
    }

    /// <summary>
    /// Minimal IRunStore stub that just counts the V22 reliability-signal calls
    /// — IncrementRateLimitEventsAsync and FlagWinnersRegressedForRunAsync.
    /// Everything else is a no-op so the AgentRunner happy path still works.
    /// </summary>
    private sealed class CountingRunStore : IRunStore
    {
        public int RateLimitIncrements { get; private set; }
        public int WinnerRegressedFlags { get; private set; }

        public Task CreateRunAsync(RunRecord run, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunProgressAsync(string runId, int completedSteps, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteRunAsync(string runId, AgentOutcome outcome, string? errorDetail, FailureReason? failureReason, CancellationToken ct) => Task.CompletedTask;
        public Task SaveStepResultAsync(StepResultRecord result, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<StepResultRecord>> GetStepResultsForCardAsync(string cardId, string? stateName, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<StepResultRecord>>([]);
        public Task<IReadOnlyList<StepResultRecord>> GetLatestRunStepResultsAsync(string cardId, string stateName, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<StepResultRecord>>([]);
        public Task UpdateRunEstimateAsync(string runId, double estimate, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunSessionStartupMsAsync(string runId, int startupMs, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateCandidateEvaluationAsync(string runId, Guid candidateGroupId, int candidateIndex, bool selected, decimal? qualityScore, string? evaluatorReasoning, CancellationToken ct) => Task.CompletedTask;

        public Task IncrementRateLimitEventsAsync(string runId, CancellationToken ct)
        {
            RateLimitIncrements++;
            return Task.CompletedTask;
        }

        public Task FlagWinnersRegressedForRunAsync(string runId, CancellationToken ct)
        {
            WinnerRegressedFlags++;
            return Task.CompletedTask;
        }

        public Task<int> GetStepAttemptCountAsync(string cardId, string stateName, string stepName, CancellationToken ct)
            => Task.FromResult(0);
    }
}
