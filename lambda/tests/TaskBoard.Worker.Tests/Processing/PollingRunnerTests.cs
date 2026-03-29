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

        boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>())
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
            new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance),
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            TestConfig,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
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

        boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<BoardCard>>(new List<BoardCard>()));

        var agentRunner = new AgentRunner(
            boardClient,
            new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance),
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            TestConfig,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
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

        boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>())
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
            new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance),
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            TestConfig,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
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
            NullLogger<PollingRunner>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await pollingRunner.RunAsync(BoardId, Workspace, TimeSpan.FromMilliseconds(50), cts.Token);

        // The loop survived the exceptions and continued polling
        Assert.True(fetchCount >= 3,
            $"Expected at least 3 fetch attempts (2 failures + 1 success), got {fetchCount}");
    }
}
