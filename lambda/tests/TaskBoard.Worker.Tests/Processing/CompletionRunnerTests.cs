using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

public class CompletionRunnerTests
{
    private const string ParentCardId = "10";
    private const string BoardId = "board-1";
    private const string Workspace = "C:/fake";
    private const string AwaitingChildrenCol = "Awaiting Children";
    private const string DoneCol = "Done";
    private const string ReadyForTestCol = "Ready for Test";
    private const string ErrorCol = "Error";

    private readonly ITaskBoardClient _boardClient;
    private readonly ICrossReferenceResolver _crossRefResolver;
    private readonly WorkflowConfig _config;
    private readonly AgentIdentity _identity;

    public CompletionRunnerTests()
    {
        _boardClient = Substitute.For<ITaskBoardClient>();
        _crossRefResolver = Substitute.For<ICrossReferenceResolver>();
        _identity = new AgentIdentity("TestBot", "machine");

        _boardClient.GetCurrentUserAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult("test-bot"));

        _config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [AwaitingChildrenCol] = new(
                    AwaitingChildrenCol, null, "children_complete", null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn(ReadyForTestCol),
                        ["ERROR"]    = TransitionTarget.ForColumn(ErrorCol),
                    },
                    PipelineOrder: 2),
                [ReadyForTestCol] = new(ReadyForTestCol, null, "agent_run", null, new()),
                [DoneCol] = new(DoneCol, null, "terminal", null, new()),
                [ErrorCol] = new(ErrorCol, null, "holding", null, new()),
            },
            Roles: new());
    }

    private CompletionRunner CreateRunner() =>
        new(_boardClient, _crossRefResolver, _config, _identity,
            NullLogger<CompletionRunner>.Instance);

    private void SetupParentCard(string column = AwaitingChildrenCol) =>
        _boardClient.GetCardAsync(ParentCardId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new BoardCard(ParentCardId, "Parent Story", "body", column)));

    private void SetupChildRefs(params string[] childIds)
    {
        IReadOnlyList<CardReference> refs = childIds
            .Select(id => new CardReference(id, "sub_item", null, $"Child {id}"))
            .ToList();
        _crossRefResolver.GetStructuredReferencesAsync(ParentCardId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(refs));
    }

    private void SetupChildCard(string childId, string column) =>
        _boardClient.GetCardAsync(childId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new BoardCard(childId, $"Child {childId}", "body", column)));

    // ── All children complete ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_AllChildrenInTerminalState_ReturnsCOMPLETE()
    {
        SetupParentCard();
        SetupChildRefs("1", "2");
        SetupChildCard("1", DoneCol);
        SetupChildCard("2", DoneCol);

        var runner = CreateRunner();
        var result = await runner.ExecuteAsync(ParentCardId, BoardId, Workspace, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Contains("2", result.ErrorDetail); // "All 2 child tasks complete"
    }

    [Fact]
    public async Task ExecuteAsync_AllChildrenComplete_TransitionsToCompleteTarget()
    {
        SetupParentCard();
        SetupChildRefs("1");
        SetupChildCard("1", DoneCol);

        var runner = CreateRunner();
        await runner.ExecuteAsync(ParentCardId, BoardId, Workspace, CancellationToken.None);

        await _boardClient.Received(1).MoveCardToColumnAsync(
            ParentCardId, ReadyForTestCol, Arg.Any<CancellationToken>());
    }

    // ── Some children pending ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_SomeChildrenPending_ReturnsNEEDS_INFO()
    {
        SetupParentCard();
        SetupChildRefs("1", "2");
        SetupChildCard("1", DoneCol);
        SetupChildCard("2", "Implementing"); // not terminal

        var runner = CreateRunner();
        var result = await runner.ExecuteAsync(ParentCardId, BoardId, Workspace, CancellationToken.None);

        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
        Assert.Contains("#2", result.ErrorDetail);
    }

    [Fact]
    public async Task ExecuteAsync_SomePending_DoesNotTransition()
    {
        SetupParentCard();
        SetupChildRefs("1", "2");
        SetupChildCard("1", DoneCol);
        SetupChildCard("2", "Designing");

        var runner = CreateRunner();
        await runner.ExecuteAsync(ParentCardId, BoardId, Workspace, CancellationToken.None);

        await _boardClient.DidNotReceive().MoveCardToColumnAsync(
            ParentCardId, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── No children ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NoChildren_ReturnsERROR()
    {
        SetupParentCard();
        SetupChildRefs(); // empty

        var runner = CreateRunner();
        var result = await runner.ExecuteAsync(ParentCardId, BoardId, Workspace, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("No tracked children", result.ErrorDetail);
    }

    [Fact]
    public async Task ExecuteAsync_NoChildren_TransitionsToErrorTarget()
    {
        SetupParentCard();
        SetupChildRefs();

        var runner = CreateRunner();
        await runner.ExecuteAsync(ParentCardId, BoardId, Workspace, CancellationToken.None);

        await _boardClient.Received(1).MoveCardToColumnAsync(
            ParentCardId, ErrorCol, Arg.Any<CancellationToken>());
    }

    // ── Child fetch failure ──────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ChildFetchFails_TreatsAsPending()
    {
        SetupParentCard();
        SetupChildRefs("1", "2");
        SetupChildCard("1", DoneCol);
        _boardClient.GetCardAsync("2", Arg.Any<CancellationToken>())
            .Returns(Task.FromException<BoardCard>(new InvalidOperationException("Not found")));

        var runner = CreateRunner();
        var result = await runner.ExecuteAsync(ParentCardId, BoardId, Workspace, CancellationToken.None);

        // Child 2 treated as pending — should not complete
        Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
    }

    // ── Wrong gate type ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CardNotInChildrenCompleteState_ReturnsERROR()
    {
        // Card is in Ready for Test, not in Awaiting Children
        _boardClient.GetCardAsync(ParentCardId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new BoardCard(ParentCardId, "Card", "body", ReadyForTestCol)));

        var runner = CreateRunner();
        var result = await runner.ExecuteAsync(ParentCardId, BoardId, Workspace, CancellationToken.None);

        Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        Assert.Contains("not a children_complete state", result.ErrorDetail);
    }

    // ── Status comment posted ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_Always_PostsStatusComment()
    {
        SetupParentCard();
        SetupChildRefs("1");
        SetupChildCard("1", DoneCol);

        var runner = CreateRunner();
        await runner.ExecuteAsync(ParentCardId, BoardId, Workspace, CancellationToken.None);

        await _boardClient.Received(1).UpsertAgentCommentAsync(
            ParentCardId,
            Arg.Is<string>(s => s.Contains("Children status")),
            Arg.Is<string>(s => s.StartsWith("<!-- completion-check:")),
            Arg.Any<CancellationToken>());
    }

    // ── Non-sub_item references ignored ─────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_OnlySubItemRefsCountAsChildren()
    {
        SetupParentCard();

        // Mix: one sub_item (done) and one mentioned_in_body (not a child)
        IReadOnlyList<CardReference> refs = new List<CardReference>
        {
            new("1", "sub_item", null, "Child 1"),
            new("2", "mentioned_in_body", null, "Related Card"),
        };
        _crossRefResolver.GetStructuredReferencesAsync(ParentCardId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(refs));
        SetupChildCard("1", DoneCol);

        var runner = CreateRunner();
        var result = await runner.ExecuteAsync(ParentCardId, BoardId, Workspace, CancellationToken.None);

        // Only child "1" counts — it's done — should complete
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }
}
