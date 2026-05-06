using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

public class DependencyGuardTests
{
    private static WorkflowConfig Config(DependencyPolicy? policy = null) =>
        new(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready for Implementation"] = new(
                    "Ready for Implementation", null, GateTypes.AgentRun, null, new(),
                    PipelineOrder: 2),
                ["Done"] = new("Done", null, GateTypes.Terminal, null, new()),
            },
            Roles: new(),
            DependencyPolicy: policy ?? new DependencyPolicy(Enabled: true));

    [Fact]
    public async Task CheckAsync_UnresolvedBlocker_ReturnsBlockedAndPostsComment()
    {
        var board = Substitute.For<ITaskBoardClient>();
        var deps = Substitute.For<ICardDependencyClient>();
        var store = Substitute.For<IDependencyWaitStore>();
        var card = new BoardCard("10", "Implement API", "", "Ready for Implementation");
        var state = Config().ResolveState(card)!;

        deps.GetBlockersAsync("10", Arg.Any<CancellationToken>())
            .Returns([new CardDependency("5", "Create database")]);
        board.GetCardAsync("5", Arg.Any<CancellationToken>())
            .Returns(new BoardCard("5", "Create database", "", "Ready for Implementation"));

        var guard = new DependencyGuard(board, deps, Config(), store, NullLogger<DependencyGuard>.Instance);

        var result = await guard.CheckAsync(card, state, "test", CancellationToken.None);

        Assert.True(result.IsBlocked);
        Assert.Equal("5", result.UnresolvedBlockers[0].CardId);
        await board.Received(1).UpsertAgentCommentAsync(
            "10",
            Arg.Is<string>(s => s.Contains("#5")),
            "<!-- aiboard:dependency-blocked -->",
            Arg.Any<CancellationToken>());
        await store.Received(1).RecordBlockedAsync(
            "10",
            Arg.Is<IReadOnlyList<CardDependency>>(b => b.Count == 1 && b[0].CardId == "5"),
            "test",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_BlockerInSatisfiedColumn_ReturnsNotBlocked()
    {
        var board = Substitute.For<ITaskBoardClient>();
        var deps = Substitute.For<ICardDependencyClient>();
        var store = Substitute.For<IDependencyWaitStore>();
        var config = Config();
        var card = new BoardCard("10", "Implement API", "", "Ready for Implementation");
        var state = config.ResolveState(card)!;

        deps.GetBlockersAsync("10", Arg.Any<CancellationToken>())
            .Returns([new CardDependency("5", "Create database")]);
        board.GetCardAsync("5", Arg.Any<CancellationToken>())
            .Returns(new BoardCard("5", "Create database", "", "Done"));

        var guard = new DependencyGuard(board, deps, config, store, NullLogger<DependencyGuard>.Instance);

        var result = await guard.CheckAsync(card, state, "test", CancellationToken.None);

        Assert.False(result.IsBlocked);
        await board.DidNotReceive().UpsertAgentCommentAsync(
            "10", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await store.Received(1).RecordBlockedAsync(
            "10",
            Arg.Is<IReadOnlyList<CardDependency>>(b => b.Count == 0),
            "test",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_DisabledPolicy_DoesNotQueryDependencies()
    {
        var board = Substitute.For<ITaskBoardClient>();
        var deps = Substitute.For<ICardDependencyClient>();
        var store = Substitute.For<IDependencyWaitStore>();
        var config = Config(new DependencyPolicy(Enabled: false));
        var card = new BoardCard("10", "Implement API", "", "Ready for Implementation");
        var state = config.ResolveState(card)!;

        var guard = new DependencyGuard(board, deps, config, store, NullLogger<DependencyGuard>.Instance);

        var result = await guard.CheckAsync(card, state, "test", CancellationToken.None);

        Assert.False(result.IsBlocked);
        await deps.DidNotReceive().GetBlockersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AgentRunner_BlockedCard_ReturnsNeedsInfoBeforeMovingInProgress()
    {
        var board = Substitute.For<ITaskBoardClient>();
        var deps = Substitute.For<ICardDependencyClient>();
        var store = Substitute.For<IDependencyWaitStore>();
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready for Implementation"] = new(
                    "Ready for Implementation", null, GateTypes.AgentRun, null,
                    new() { ["IN_PROGRESS"] = TransitionTarget.ForColumn("Implementing") },
                    Steps: [new WorkflowStep("implement", "impl", TaskPrompt: "Do it.")]),
                ["Implementing"] = new("Implementing", null, GateTypes.InProgress, null, new()),
                ["Done"] = new("Done", null, GateTypes.Terminal, null, new()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["impl"] = new("model", "system", []),
            },
            DependencyPolicy: new DependencyPolicy(Enabled: true));
        var card = new BoardCard("10", "Implement API", "", "Ready for Implementation");
        board.GetBoardCardsAsync("board", Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>>())
            .Returns([card]);
        deps.GetBlockersAsync("10", Arg.Any<CancellationToken>())
            .Returns([new CardDependency("5", "Create database")]);
        board.GetCardAsync("5", Arg.Any<CancellationToken>())
            .Returns(new BoardCard("5", "Create database", "", "Ready for Implementation"));

        var guard = new DependencyGuard(board, deps, config, store, NullLogger<DependencyGuard>.Instance);
        var runner = new AgentRunner(
            board,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            config,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "machine"),
            new UpdateFileProcessor(board, config, new AgentIdentity("Test", "Agent", "machine"), NullLogger<UpdateFileProcessor>.Instance),
            NullRunStore.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            TaskBoard.Worker.Tests.Helpers.TestTenant.Instance,
            NullLogger<AgentRunner>.Instance,
            dependencyGuard: guard);

        var result = await runner.ExecuteAsync("10", "board", Path.GetTempPath(), CancellationToken.None);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        await board.DidNotReceive().MoveCardToColumnAsync("10", "Implementing", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MergeRunner_BlockedCard_ReturnsNeedsInfoBeforeMovingInProgress()
    {
        var board = Substitute.For<ITaskBoardClient>();
        var deps = Substitute.For<ICardDependencyClient>();
        var store = Substitute.For<IDependencyWaitStore>();
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Approved"] = new(
                    "Approved", null, GateTypes.SystemMerge, null,
                    new() { ["IN_PROGRESS"] = TransitionTarget.ForColumn("Merging") },
                    PipelineOrder: 4),
                ["Merging"] = new("Merging", null, GateTypes.InProgress, null, new()),
                ["Done"] = new("Done", null, GateTypes.Terminal, null, new()),
            },
            Roles: new(),
            DependencyPolicy: new DependencyPolicy(Enabled: true));
        var card = new BoardCard("10", "Implement API", "", "Approved");
        board.GetBoardCardsAsync("board", Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>>())
            .Returns([card]);
        deps.GetBlockersAsync("10", Arg.Any<CancellationToken>())
            .Returns([new CardDependency("5", "Create database")]);
        board.GetCardAsync("5", Arg.Any<CancellationToken>())
            .Returns(new BoardCard("5", "Create database", "", "Approved"));

        var guard = new DependencyGuard(board, deps, config, store, NullLogger<DependencyGuard>.Instance);
        var runner = new MergeRunner(
            board,
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            config,
            new AgentIdentity("Test", "Agent", "machine"),
            new StubCrossReferenceResolver(),
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            NullLogger<MergeRunner>.Instance,
            guard);

        var result = await runner.ExecuteAsync("10", "board", Path.GetTempPath(), CancellationToken.None);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        await board.DidNotReceive().MoveCardToColumnAsync("10", "Merging", Arg.Any<CancellationToken>());
    }
}
