using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

public class TransitionExecutorTests
{
    private const string CardId = "card-42";

    private readonly ITaskBoardClient _boardClient = Substitute.For<ITaskBoardClient>();
    private readonly ILogger _logger = NullLogger.Instance;

    // --- Action dispatch ---

    [Fact]
    public async Task ExecuteAsync_MoveToColumn_CallsMoveCardToColumn()
    {
        var target = TransitionTarget.ForColumn("Designing");
        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None);
        await _boardClient.Received(1).MoveCardToColumnAsync(CardId, "Designing", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_AddLabel_CallsAddLabel()
    {
        var target = new TransitionTarget([new TransitionAction(ActionTypes.AddLabel, "wip")]);
        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None);
        await _boardClient.Received(1).AddLabelAsync(CardId, "wip", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_RemoveLabel_CallsRemoveLabel()
    {
        var target = new TransitionTarget([new TransitionAction(ActionTypes.RemoveLabel, "wip")]);
        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None);
        await _boardClient.Received(1).RemoveLabelAsync(CardId, "wip", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_Assign_CallsAssign()
    {
        var target = new TransitionTarget([new TransitionAction(ActionTypes.Assign, "alice")]);
        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None);
        await _boardClient.Received(1).AssignAsync(CardId, "alice", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_Unassign_CallsUnassignWithValue()
    {
        var target = new TransitionTarget([new TransitionAction(ActionTypes.Unassign, "alice")]);
        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None);
        await _boardClient.Received(1).UnassignAsync(CardId, "alice", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_UnassignNoValue_CallsUnassignWithNull()
    {
        var target = new TransitionTarget([new TransitionAction(ActionTypes.Unassign)]);
        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None);
        await _boardClient.Received(1).UnassignAsync(CardId, null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_SetField_CallsSetField()
    {
        var target = new TransitionTarget([new TransitionAction(ActionTypes.SetField, "backend", Field: "team")]);
        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None);
        await _boardClient.Received(1).SetFieldAsync(CardId, "team", "backend", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_ClearField_CallsClearField()
    {
        var target = new TransitionTarget([new TransitionAction(ActionTypes.ClearField, Field: "team")]);
        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None);
        await _boardClient.Received(1).ClearFieldAsync(CardId, "team", Arg.Any<CancellationToken>());
    }

    // --- Multiple actions in order ---

    [Fact]
    public async Task ExecuteAsync_MultipleActions_ExecutesAllInOrder()
    {
        var callOrder = new List<string>();
        _boardClient.MoveCardToColumnAsync(CardId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("move"));
        _boardClient.AddLabelAsync(CardId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("addLabel"));
        _boardClient.AssignAsync(CardId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(_ => callOrder.Add("assign"));

        var target = new TransitionTarget([
            new TransitionAction(ActionTypes.MoveToColumn, "Designing"),
            new TransitionAction(ActionTypes.AddLabel, "wip"),
            new TransitionAction(ActionTypes.Assign, "alice"),
        ]);

        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None);

        Assert.Equal(["move", "addLabel", "assign"], callOrder);
    }

    // --- Failure semantics ---

    [Fact]
    public async Task ExecuteAsync_MoveToColumnFails_PropagatesException()
    {
        _boardClient.MoveCardToColumnAsync(CardId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("column not found"));

        var target = TransitionTarget.ForColumn("Designing");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_AddLabelFails_ContinuesExecution()
    {
        _boardClient.AddLabelAsync(CardId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("label not found"));

        var target = new TransitionTarget([
            new TransitionAction(ActionTypes.AddLabel, "wip"),
            new TransitionAction(ActionTypes.MoveToColumn, "Designing"),
        ]);

        // Should not throw — addLabel failure is lenient
        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None);

        // The column move after the label failure should still happen
        await _boardClient.Received(1).MoveCardToColumnAsync(CardId, "Designing", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_AssignFails_ContinuesExecution()
    {
        _boardClient.AssignAsync(CardId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("user not found"));
        _boardClient.RemoveLabelAsync(CardId, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var target = new TransitionTarget([
            new TransitionAction(ActionTypes.Assign, "bot"),
            new TransitionAction(ActionTypes.RemoveLabel, "wip"),
        ]);

        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None);

        // RemoveLabel should still be called despite Assign failing
        await _boardClient.Received(1).RemoveLabelAsync(CardId, "wip", Arg.Any<CancellationToken>());
    }

    // --- Template resolution ---

    [Fact]
    public async Task ExecuteAsync_TemplateInValue_ResolvesBeforeDispatch()
    {
        var context = new Dictionary<string, string> { ["agent"] = "bot-user" };
        var target = new TransitionTarget([new TransitionAction(ActionTypes.Assign, "{{agent}}")]);

        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None, context);

        await _boardClient.Received(1).AssignAsync(CardId, "bot-user", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_TemplateInField_ResolvesBeforeDispatch()
    {
        var context = new Dictionary<string, string> { ["fieldName"] = "team" };
        var target = new TransitionTarget([new TransitionAction(ActionTypes.ClearField, Field: "{{fieldName}}")]);

        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None, context);

        await _boardClient.Received(1).ClearFieldAsync(CardId, "team", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_NoTemplateContext_LiteralValuePassedThrough()
    {
        var target = new TransitionTarget([new TransitionAction(ActionTypes.Assign, "{{agent}}")]);

        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None);

        // No context — {{agent}} is passed literally
        await _boardClient.Received(1).AssignAsync(CardId, "{{agent}}", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_MultipleTemplateKeys_AllResolved()
    {
        var context = new Dictionary<string, string>
        {
            ["agent"] = "bot-user",
            ["field"] = "team",
            ["value"] = "backend",
        };
        var target = new TransitionTarget([
            new TransitionAction(ActionTypes.SetField, "{{value}}", Field: "{{field}}"),
            new TransitionAction(ActionTypes.Assign, "{{agent}}"),
        ]);

        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None, context);

        await _boardClient.Received(1).SetFieldAsync(CardId, "team", "backend", Arg.Any<CancellationToken>());
        await _boardClient.Received(1).AssignAsync(CardId, "bot-user", Arg.Any<CancellationToken>());
    }

    // --- Empty target ---

    [Fact]
    public async Task ExecuteAsync_EmptyActions_NoCallsToClient()
    {
        var target = new TransitionTarget([]);

        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None);

        await _boardClient.DidNotReceiveWithAnyArgs().MoveCardToColumnAsync(default!, default!, default);
        await _boardClient.DidNotReceiveWithAnyArgs().AddLabelAsync(default!, default!, default);
    }

    // --- Unknown action type ---

    [Fact]
    public async Task ExecuteAsync_UnknownActionType_LogsWarningAndContinues()
    {
        var target = new TransitionTarget([
            new TransitionAction("unknown-action-type", "some-value"),
            new TransitionAction(ActionTypes.MoveToColumn, "Designing"),
        ]);

        // Should not throw — unknown type is lenient
        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None);

        // Column move should still execute
        await _boardClient.Received(1).MoveCardToColumnAsync(CardId, "Designing", Arg.Any<CancellationToken>());
    }
}
