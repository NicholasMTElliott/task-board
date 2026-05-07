using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
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
            DependencyPolicy: policy ?? new DependencyPolicy(
                Enabled: true,
                EnforcedStates: ["Ready for Implementation"]));

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
            "<!-- agent-dependency-blocked -->",
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
    public async Task CheckAsync_EnabledButEmptyEnforcedStates_DoesNotEnforce()
    {
        // Concern 12: there is no in-code default for EnforcedStates. If the
        // operator enables the policy but doesn't list any states/columns,
        // the policy is a no-op rather than implicitly enforcing something.
        var board = Substitute.For<ITaskBoardClient>();
        var deps = Substitute.For<ICardDependencyClient>();
        var store = Substitute.For<IDependencyWaitStore>();
        var config = Config(new DependencyPolicy(Enabled: true, EnforcedStates: null));
        var card = new BoardCard("10", "Implement API", "", "Ready for Implementation");
        var state = config.ResolveState(card)!;

        var guard = new DependencyGuard(board, deps, config, store, NullLogger<DependencyGuard>.Instance);

        var result = await guard.CheckAsync(card, state, "test", CancellationToken.None);

        Assert.False(result.IsBlocked);
        await deps.DidNotReceive().GetBlockersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_ClosedAsCompletedBlocker_SkipsHydrationCall()
    {
        // Bug 6: a blocker reported as closed-and-completed by the dependency
        // API doesn't need a column lookup — it's satisfied on state alone.
        var board = Substitute.For<ITaskBoardClient>();
        var deps = Substitute.For<ICardDependencyClient>();
        var store = Substitute.For<IDependencyWaitStore>();
        var card = new BoardCard("10", "Implement API", "", "Ready for Implementation");
        var state = Config().ResolveState(card)!;

        deps.GetBlockersAsync("10", Arg.Any<CancellationToken>())
            .Returns([new CardDependency("5", "Done blocker", IsClosed: true, StateReason: "completed")]);

        var guard = new DependencyGuard(board, deps, Config(), store, NullLogger<DependencyGuard>.Instance);

        var result = await guard.CheckAsync(card, state, "test", CancellationToken.None);

        Assert.False(result.IsBlocked);
        await board.DidNotReceive().GetCardAsync("5", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CheckAsync_ClosedAsNotPlannedBlocker_StillBlocks()
    {
        // Bug 6 corollary: closed-as-not-planned is NOT satisfied — the
        // upstream issue was abandoned, not completed, so dependent work
        // shouldn't proceed.
        var board = Substitute.For<ITaskBoardClient>();
        var deps = Substitute.For<ICardDependencyClient>();
        var store = Substitute.For<IDependencyWaitStore>();
        var card = new BoardCard("10", "Implement API", "", "Ready for Implementation");
        var state = Config().ResolveState(card)!;

        deps.GetBlockersAsync("10", Arg.Any<CancellationToken>())
            .Returns([new CardDependency("5", "Abandoned", IsClosed: true, StateReason: "not_planned")]);
        board.GetCardAsync("5", Arg.Any<CancellationToken>())
            .Returns(new BoardCard("5", "Abandoned", "", "Backlog"));

        var guard = new DependencyGuard(board, deps, Config(), store, NullLogger<DependencyGuard>.Instance);

        var result = await guard.CheckAsync(card, state, "test", CancellationToken.None);

        Assert.True(result.IsBlocked);
    }

    [Fact]
    public async Task CheckAsync_ContractExceptionPropagates_NotSwallowedAsTransient()
    {
        // Bug 5: API shape divergence is fatal — DependencyGuard must NOT
        // catch DependencyApiContractException. Operators need to notice the
        // upstream broke rather than silently treating every blocked card as
        // clear.
        var board = Substitute.For<ITaskBoardClient>();
        var deps = Substitute.For<ICardDependencyClient>();
        var store = Substitute.For<IDependencyWaitStore>();
        var card = new BoardCard("10", "Implement API", "", "Ready for Implementation");
        var state = Config().ResolveState(card)!;

        deps.GetBlockersAsync("10", Arg.Any<CancellationToken>())
            .ThrowsAsync(new DependencyApiContractException("API shape changed"));

        var guard = new DependencyGuard(board, deps, Config(), store, NullLogger<DependencyGuard>.Instance);

        await Assert.ThrowsAsync<DependencyApiContractException>(() =>
            guard.CheckAsync(card, state, "test", CancellationToken.None));
    }

    [Fact]
    public async Task CheckAsync_TransientLookupFailure_SwallowedAsNotBlocked()
    {
        // The opposite of the contract test: transient errors (auth, 5xx,
        // network) are caught and the pipeline keeps moving.
        var board = Substitute.For<ITaskBoardClient>();
        var deps = Substitute.For<ICardDependencyClient>();
        var store = Substitute.For<IDependencyWaitStore>();
        var card = new BoardCard("10", "Implement API", "", "Ready for Implementation");
        var state = Config().ResolveState(card)!;

        deps.GetBlockersAsync("10", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("gh exited with code 1"));

        var guard = new DependencyGuard(board, deps, Config(), store, NullLogger<DependencyGuard>.Instance);

        var result = await guard.CheckAsync(card, state, "test", CancellationToken.None);

        Assert.False(result.IsBlocked);
    }

    [Fact]
    public async Task CheckAsync_BoardCardCache_PopulatedAndReusedAcrossBlockers()
    {
        // Bug 6: per-cycle hydration cache. The same blocker referenced by
        // two enforced cards in one polling pass should only be fetched once.
        var board = Substitute.For<ITaskBoardClient>();
        var deps = Substitute.For<ICardDependencyClient>();
        var store = Substitute.For<IDependencyWaitStore>();
        var card1 = new BoardCard("10", "Card A", "", "Ready for Implementation");
        var card2 = new BoardCard("11", "Card B", "", "Ready for Implementation");
        var state = Config().ResolveState(card1)!;

        deps.GetBlockersAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([new CardDependency("5", "Shared blocker")]);
        board.GetCardAsync("5", Arg.Any<CancellationToken>())
            .Returns(new BoardCard("5", "Shared", "", "Ready for Implementation"));

        var guard = new DependencyGuard(board, deps, Config(), store, NullLogger<DependencyGuard>.Instance);
        var cache = new Dictionary<string, BoardCard>(StringComparer.Ordinal);

        await guard.CheckAsync(card1, state, "polling", CancellationToken.None, cache);
        await guard.CheckAsync(card2, state, "polling", CancellationToken.None, cache);

        await board.Received(1).GetCardAsync("5", Arg.Any<CancellationToken>());
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
            DependencyPolicy: new DependencyPolicy(
                Enabled: true,
                EnforcedStates: ["Ready for Implementation"]));
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
            new AgentIdentity("Agent", "machine"),
            new UpdateFileProcessor(board, config, new AgentIdentity("Agent", "machine"), NullLogger<UpdateFileProcessor>.Instance),
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
            DependencyPolicy: new DependencyPolicy(
                Enabled: true,
                EnforcedStates: ["Approved"]));
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
            new AgentIdentity("Agent", "machine"),
            new StubCrossReferenceResolver(),
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            NullLogger<MergeRunner>.Instance,
            guard);

        var result = await runner.ExecuteAsync("10", "board", Path.GetTempPath(), CancellationToken.None);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        await board.DidNotReceive().MoveCardToColumnAsync("10", "Merging", Arg.Any<CancellationToken>());
    }
}
