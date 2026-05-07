using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using TaskBoard.Worker.Tests.Helpers;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Tests for graceful shutdown behavior in <see cref="PollingRunner"/>.
/// </summary>
public class ShutdownPollingRunnerTests
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

    private static PollingRunner BuildPollingRunner(
        ITaskBoardClient boardClient,
        ShutdownCoordinator? coordinator = null)
    {
        var agentRunner = new AgentRunner(
            boardClient,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            TestWorkflowConfigProvider.Create(TestConfig),
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(boardClient, TestWorkflowConfigProvider.Create(TestConfig), new AgentIdentity("Test", "Agent", "TestMachine"), NullLogger<UpdateFileProcessor>.Instance),
            NullRunStore.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            TaskBoard.Worker.Tests.Helpers.TestTenant.Instance,
            NullLogger<AgentRunner>.Instance);

        var mergeRunner = new MergeRunner(
            boardClient,
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            TestConfig,
            new AgentIdentity("Test", "Agent", "TestMachine"),
            Substitute.For<ICrossReferenceResolver>(),
            Substitute.For<IAgentExecutorResolver>(),
            NullLogger<MergeRunner>.Instance);

        return new PollingRunner(
            boardClient,
            agentRunner,
            mergeRunner,
            new CompletionRunner(boardClient, new StubCrossReferenceResolver(), TestConfig, new AgentIdentity("Test", "Agent", "TestMachine"), NullLogger<CompletionRunner>.Instance),
            TestConfig,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            NullLogger<PollingRunner>.Instance,
            coordinator);
    }

    /// <summary>
    /// When shutdown is requested before the loop starts, the runner exits immediately
    /// without processing any cards.
    /// </summary>
    [Fact]
    public async Task RunAsync_ShutdownPreSet_ExitsWithoutProcessing()
    {
        var boardClient = Substitute.For<ITaskBoardClient>();
        boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(Task.FromResult<IReadOnlyList<BoardCard>>(new List<BoardCard>
            {
                new("1", "Ready Card", "body", "Ready for Design"),
            }));

        using var coordinator = new ShutdownCoordinator();
        coordinator.RequestShutdown(); // shutdown before run starts

        var runner = BuildPollingRunner(boardClient, coordinator);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await runner.RunAsync(BoardId, Workspace, TimeSpan.FromMilliseconds(50), cts.Token);

        // No card should have been processed — MoveCardToColumnAsync (IN_PROGRESS) not called
        await boardClient.DidNotReceive().MoveCardToColumnAsync(Arg.Any<string>(), "Designing", Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// When shutdown is requested during an idle delay, the delay is interrupted promptly
    /// and the runner exits cleanly.
    /// </summary>
    [Fact]
    public async Task RunAsync_ShutdownDuringIdleDelay_ExitsPromptly()
    {
        var boardClient = Substitute.For<ITaskBoardClient>();

        // No cards available — runner goes into idle delay
        boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(Task.FromResult<IReadOnlyList<BoardCard>>(new List<BoardCard>()));

        using var coordinator = new ShutdownCoordinator();
        var runner = BuildPollingRunner(boardClient, coordinator);

        // A very long poll interval so the test would hang if shutdown doesn't interrupt it
        var runTask = runner.RunAsync(BoardId, Workspace, TimeSpan.FromSeconds(60), CancellationToken.None);

        // Give the runner time to enter the idle delay
        await Task.Delay(100);

        // Request graceful shutdown — IdleToken is cancelled, which interrupts the delay
        coordinator.RequestShutdown();

        // Runner should exit promptly (well within a reasonable timeout)
        var completed = await Task.WhenAny(runTask, Task.Delay(TimeSpan.FromSeconds(3)));
        Assert.True(completed == runTask, "Runner did not exit promptly after shutdown request");
        await runTask; // should complete cleanly
    }

    /// <summary>
    /// With no coordinator, the runner behaves exactly as before (backward compatibility).
    /// </summary>
    [Fact]
    public async Task RunAsync_NoCoordinator_CancellationTokenStillWorks()
    {
        var boardClient = Substitute.For<ITaskBoardClient>();
        boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(Task.FromResult<IReadOnlyList<BoardCard>>(new List<BoardCard>()));

        var runner = BuildPollingRunner(boardClient); // no coordinator

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // cancel immediately

        // Should return without hanging
        await runner.RunAsync(BoardId, Workspace, TimeSpan.FromSeconds(60), cts.Token);
    }
}
