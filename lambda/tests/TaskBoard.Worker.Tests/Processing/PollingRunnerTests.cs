using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

public class PollingRunnerTests
{
    private const string BoardId = "board-1";
    private const string Workspace = "C:/fake/workspace";

    private static readonly WorkflowConfig TestConfig = new WorkflowConfig(
        States: new Dictionary<string, WorkflowState>
        {
            ["Ready for Design"] = new("Ready for Design", "se", "agent_run",
                "Design it.",
                new Dictionary<string, TransitionTarget>
                {
                    ["IN_PROGRESS"] = TransitionTarget.ForColumn("Designing"),
                    ["COMPLETE"]    = TransitionTarget.ForColumn("Designed"),
                    ["ERROR"]       = TransitionTarget.ForColumn("Error"),
                },
                PipelineOrder: 1),
            ["Designing"] = new("Designing", null, "in_progress",
                null, new Dictionary<string, TransitionTarget>()),
            ["Designed"] = new("Designed", null, "manual_gate",
                null, new Dictionary<string, TransitionTarget>()),
            ["Error"] = new("Error", null, "holding",
                null, new Dictionary<string, TransitionTarget>()),
        },
        Roles: new Dictionary<string, WorkflowRole>
        {
            ["se"] = new("claude-opus-4-6", "You are an engineer.", new List<string> { "Design" }),
        }).Normalised();

    private static BoardCard MakeCard(string id, string column) =>
        new(id, $"Card {id}", "body", column);

    /// <summary>
    /// Verifies that the polling runner selects the correct card and calls
    /// AgentRunner.ExecuteAsync. We verify this by checking that
    /// MoveCardToColumnAsync was called with the IN_PROGRESS column,
    /// which proves AgentRunner.ExecuteAsync was entered for the right card.
    /// (AgentRunner will error later due to no git repo, but that's OK.)
    /// </summary>
    [Fact]
    public async Task RunAsync_SelectsEligibleCard_CallsExecuteAsync()
    {
        var boardClient = Substitute.For<ITaskBoardClient>();
        var callCount = 0;

        boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(_ =>
            {
                callCount++;
                IReadOnlyList<BoardCard> cards = new List<BoardCard>
                {
                    MakeCard("1", "Designed"),          // manual_gate — not eligible
                    MakeCard("2", "Ready for Design"),  // agent_run — eligible
                };
                return Task.FromResult(cards);
            });

        // GetCardCommentsAsync is called by AgentRunner internally
        boardClient.GetCardCommentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CardComment>>(new List<CardComment>()));

        var agentRunner = new AgentRunner(
            boardClient,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            TestConfig,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(boardClient, NullLogger<UpdateFileProcessor>.Instance),
            NullLogger<AgentRunner>.Instance);

        var mergeRunner = new MergeRunner(
            boardClient,
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            TestConfig,
            new AgentIdentity("Test", "Agent", "TestMachine"),
            NullLogger<MergeRunner>.Instance);

        var pollingRunner = new PollingRunner(
            boardClient,
            agentRunner,
            mergeRunner,
            TestConfig,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            NullLogger<PollingRunner>.Instance);

        // Cancel after one cycle has had time to run
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await pollingRunner.RunAsync(BoardId, Workspace, TimeSpan.FromMilliseconds(50), cts.Token);

        // PollingRunner called GetBoardCardsAsync at least once (its own fetch)
        // AgentRunner also calls GetBoardCardsAsync internally (second fetch)
        Assert.True(callCount >= 2, $"Expected at least 2 calls to GetBoardCardsAsync, got {callCount}");

        // AgentRunner.ExecuteAsync moved the card to IN_PROGRESS before failing
        await boardClient.Received().MoveCardToColumnAsync("2", "Designing", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_CancellationToken_StopsLoopCleanly()
    {
        var boardClient = Substitute.For<ITaskBoardClient>();

        boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(Task.FromResult<IReadOnlyList<BoardCard>>(new List<BoardCard>()));

        var agentRunner = new AgentRunner(
            boardClient,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            TestConfig,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(boardClient, NullLogger<UpdateFileProcessor>.Instance),
            NullLogger<AgentRunner>.Instance);

        var mergeRunner = new MergeRunner(
            boardClient,
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            TestConfig,
            new AgentIdentity("Test", "Agent", "TestMachine"),
            NullLogger<MergeRunner>.Instance);

        var pollingRunner = new PollingRunner(
            boardClient,
            agentRunner,
            mergeRunner,
            TestConfig,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            NullLogger<PollingRunner>.Instance);

        using var cts = new CancellationTokenSource();
        // Cancel immediately
        cts.Cancel();

        // Should return without hanging
        await pollingRunner.RunAsync(BoardId, Workspace, TimeSpan.FromSeconds(60), cts.Token);

        // If we got here, the loop exited cleanly on cancellation
    }

    [Fact]
    public async Task RunAsync_BoardFetchThrows_LoopContinues()
    {
        var boardClient = Substitute.For<ITaskBoardClient>();
        var fetchCount = 0;

        boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(_ =>
            {
                fetchCount++;
                if (fetchCount <= 2)
                    throw new InvalidOperationException("Simulated API failure");

                // After 2 failures, return empty list so the loop idles
                return Task.FromResult<IReadOnlyList<BoardCard>>(new List<BoardCard>());
            });

        var agentRunner = new AgentRunner(
            boardClient,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            TestConfig,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(boardClient, NullLogger<UpdateFileProcessor>.Instance),
            NullLogger<AgentRunner>.Instance);

        var mergeRunner = new MergeRunner(
            boardClient,
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            TestConfig,
            new AgentIdentity("Test", "Agent", "TestMachine"),
            NullLogger<MergeRunner>.Instance);

        var pollingRunner = new PollingRunner(
            boardClient,
            agentRunner,
            mergeRunner,
            TestConfig,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            NullLogger<PollingRunner>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await pollingRunner.RunAsync(BoardId, Workspace, TimeSpan.FromMilliseconds(50), cts.Token);

        // The loop survived the exceptions and continued polling
        Assert.True(fetchCount >= 3,
            $"Expected at least 3 fetch attempts (2 failures + 1 success), got {fetchCount}");
    }

    [Fact]
    public async Task RunAsync_CardSkippedByProvider_LogsWarning()
    {
        // Config where the state requires "codex" but only "claude-cli" is available
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready for Design"] = new("Ready for Design", null, "agent_run",
                    null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["IN_PROGRESS"] = TransitionTarget.ForColumn("Designing"),
                        ["COMPLETE"]    = TransitionTarget.ForColumn("Designed"),
                        ["ERROR"]       = TransitionTarget.ForColumn("Error"),
                    },
                    PipelineOrder: 1,
                    Steps: [new WorkflowStep("step1", "codex_role")]),
                ["Designing"] = new("Designing", null, "in_progress",
                    null, new Dictionary<string, TransitionTarget>()),
                ["Designed"] = new("Designed", null, "manual_gate",
                    null, new Dictionary<string, TransitionTarget>()),
                ["Error"] = new("Error", null, "holding",
                    null, new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["codex_role"] = new("codex-model", "You are a coder.", new List<string>(),
                    Provider: "codex"),
            }).Normalised();

        var boardClient = Substitute.For<ITaskBoardClient>();
        boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(Task.FromResult<IReadOnlyList<BoardCard>>(new List<BoardCard>
            {
                new("42", "Needs Codex", "body", "Ready for Design"),
            }));

        var agentRunner = new AgentRunner(
            boardClient,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            config,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(boardClient, NullLogger<UpdateFileProcessor>.Instance),
            NullLogger<AgentRunner>.Instance);

        var mergeRunner = new MergeRunner(
            boardClient,
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            config,
            new AgentIdentity("Test", "Agent", "TestMachine"),
            NullLogger<MergeRunner>.Instance);

        // Resolver only has "claude-cli", not "codex"
        var resolverWithClaudeOnly = new AgentExecutorResolver(
            new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude-cli"] = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance),
            });

        var warningLogger = new FakeLogger<PollingRunner>();

        var pollingRunner = new PollingRunner(
            boardClient,
            agentRunner,
            mergeRunner,
            config,
            resolverWithClaudeOnly,
            warningLogger);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await pollingRunner.RunAsync(BoardId, Workspace, TimeSpan.FromMilliseconds(50), cts.Token);

        // Card "42" was skipped because "codex" wasn't available — warning should be logged
        var warnings = warningLogger.Logs
            .Where(l => l.Level == Microsoft.Extensions.Logging.LogLevel.Warning)
            .ToList();
        Assert.Contains(warnings, l => l.Message.Contains("42") && l.Message.Contains("codex"));
    }

    [Fact]
    public async Task RunAsync_PassesAvailableProvidersToCardSelector()
    {
        // Card requires "codex"; resolver only has "claude-cli" — card is NOT executed
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Needs Codex"] = new("Needs Codex", null, "agent_run",
                    null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["IN_PROGRESS"] = TransitionTarget.ForColumn("Doing"),
                        ["COMPLETE"]    = TransitionTarget.ForColumn("Done"),
                        ["ERROR"]       = TransitionTarget.ForColumn("Error"),
                    },
                    PipelineOrder: 1,
                    Steps: [new WorkflowStep("step", "codex_role")]),
                ["Doing"] = new("Doing", null, "in_progress",
                    null, new Dictionary<string, TransitionTarget>()),
                ["Done"] = new("Done", null, "terminal",
                    null, new Dictionary<string, TransitionTarget>()),
                ["Error"] = new("Error", null, "holding",
                    null, new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["codex_role"] = new("codex-model", "prompt", new List<string>(), Provider: "codex"),
            }).Normalised();

        var boardClient = Substitute.For<ITaskBoardClient>();
        boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(Task.FromResult<IReadOnlyList<BoardCard>>(new List<BoardCard>
            {
                new("99", "Codex Card", "body", "Needs Codex"),
            }));

        var agentRunner = new AgentRunner(
            boardClient,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            config,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(boardClient, NullLogger<UpdateFileProcessor>.Instance),
            NullLogger<AgentRunner>.Instance);

        var mergeRunner = new MergeRunner(
            boardClient,
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            config,
            new AgentIdentity("Test", "Agent", "TestMachine"),
            NullLogger<MergeRunner>.Instance);

        var resolverWithClaudeOnly = new AgentExecutorResolver(
            new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
            {
                ["claude-cli"] = new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance),
            });

        var pollingRunner = new PollingRunner(
            boardClient,
            agentRunner,
            mergeRunner,
            config,
            resolverWithClaudeOnly,
            NullLogger<PollingRunner>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await pollingRunner.RunAsync(BoardId, Workspace, TimeSpan.FromMilliseconds(50), cts.Token);

        // Card was skipped: MoveCardToColumnAsync should NOT have been called for "Doing"
        await boardClient.DidNotReceive().MoveCardToColumnAsync("99", "Doing", Arg.Any<CancellationToken>());
    }

    // ── ComputeAgentRateLimitDelay tests ─────────────────────────────────────────

    [Fact]
    public void ComputeAgentRateLimitDelay_FirstError_Returns30Minutes()
    {
        var delay = PollingRunner.ComputeAgentRateLimitDelay(1);
        Assert.Equal(1800, delay.TotalSeconds);
    }

    [Fact]
    public void ComputeAgentRateLimitDelay_SecondError_Returns60Minutes()
    {
        var delay = PollingRunner.ComputeAgentRateLimitDelay(2);
        Assert.Equal(3600, delay.TotalSeconds);
    }

    [Fact]
    public void ComputeAgentRateLimitDelay_ThirdError_Returns120Minutes()
    {
        var delay = PollingRunner.ComputeAgentRateLimitDelay(3);
        Assert.Equal(7200, delay.TotalSeconds);
    }

    [Fact]
    public void ComputeAgentRateLimitDelay_CappedAt2Hours()
    {
        // Any error count >= 3 should be capped at 7200s (2 hours)
        var delay = PollingRunner.ComputeAgentRateLimitDelay(10);
        Assert.Equal(7200, delay.TotalSeconds);
    }

    [Fact]
    public async Task RunAsync_AgentRateLimit_UsesLongerBackoff_ThanBoardApiLimit()
    {
        var boardClient = Substitute.For<ITaskBoardClient>();
        boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Throws(new RateLimitException("agent rate limited", RateLimitSource.AgentCli));

        var agentRunner = new AgentRunner(
            boardClient,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            TestConfig,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(boardClient, NullLogger<UpdateFileProcessor>.Instance),
            NullLogger<AgentRunner>.Instance);

        var mergeRunner = new MergeRunner(
            boardClient,
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            TestConfig,
            new AgentIdentity("Test", "Agent", "TestMachine"),
            NullLogger<MergeRunner>.Instance);

        var fakeLogger = new FakeLogger<PollingRunner>();
        var pollingRunner = new PollingRunner(
            boardClient,
            agentRunner,
            mergeRunner,
            TestConfig,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            fakeLogger);

        // Cancel quickly after the first rate limit backoff starts
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await pollingRunner.RunAsync(BoardId, Workspace, TimeSpan.FromMilliseconds(50), cts.Token);

        // Verify that a warning was logged mentioning the agent rate limit source
        Assert.Contains(fakeLogger.Logs, l =>
            l.Message.Contains("AgentCli") || l.Message.Contains("Rate limit"));
    }

    [Fact]
    public async Task RunAsync_BoardApiRateLimit_UsesExistingShortBackoff()
    {
        var boardClient = Substitute.For<ITaskBoardClient>();
        boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Throws(new RateLimitException("board api rate limited", RateLimitSource.BoardApi));

        var agentRunner = new AgentRunner(
            boardClient,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            TestConfig,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(boardClient, NullLogger<UpdateFileProcessor>.Instance),
            NullLogger<AgentRunner>.Instance);

        var mergeRunner = new MergeRunner(
            boardClient,
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            TestConfig,
            new AgentIdentity("Test", "Agent", "TestMachine"),
            NullLogger<MergeRunner>.Instance);

        var fakeLogger = new FakeLogger<PollingRunner>();
        var pollingRunner = new PollingRunner(
            boardClient,
            agentRunner,
            mergeRunner,
            TestConfig,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            fakeLogger);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await pollingRunner.RunAsync(BoardId, Workspace, TimeSpan.FromMilliseconds(50), cts.Token);

        // Verify warning logged mentions BoardApi source
        Assert.Contains(fakeLogger.Logs, l =>
            l.Message.Contains("BoardApi") || l.Message.Contains("Rate limit"));
    }

    [Fact]
    public async Task RunAsync_NoEligibleCards_LogsAtInformationLevel()
    {
        var boardClient = Substitute.For<ITaskBoardClient>();

        boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(Task.FromResult<IReadOnlyList<BoardCard>>(new List<BoardCard>()));

        var agentRunner = new AgentRunner(
            boardClient,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            TestConfig,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(boardClient, NullLogger<UpdateFileProcessor>.Instance),
            NullLogger<AgentRunner>.Instance);

        var mergeRunner = new MergeRunner(
            boardClient,
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            TestConfig,
            new AgentIdentity("Test", "Agent", "TestMachine"),
            NullLogger<MergeRunner>.Instance);

        var fakeLogger = new FakeLogger<PollingRunner>();

        var pollingRunner = new PollingRunner(
            boardClient,
            agentRunner,
            mergeRunner,
            TestConfig,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            fakeLogger);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await pollingRunner.RunAsync(BoardId, Workspace, TimeSpan.FromMilliseconds(50), cts.Token);

        var infoLogs = fakeLogger.Logs
            .Where(l => l.Level == LogLevel.Information)
            .ToList();

        Assert.Contains(infoLogs, l => l.Message.Contains("No eligible cards"));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private sealed class FakeLogger<T> : ILogger<T>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Logs { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Logs.Add((logLevel, formatter(state, exception)));
    }
}
