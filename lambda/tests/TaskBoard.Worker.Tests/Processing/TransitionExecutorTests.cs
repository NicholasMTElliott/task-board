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

    // --- Unresolved template detection ---

    [Fact]
    public async Task ExecuteAsync_UnresolvedTemplateInValue_SkipsAction()
    {
        // Context does not contain "estimation" — {{estimation}} remains unresolved
        var context = new Dictionary<string, string> { ["other"] = "x" };
        var target = new TransitionTarget([new TransitionAction(ActionTypes.SetField, "{{estimation}}", Field: "Estimate")]);

        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None, context);

        // SetFieldAsync should NOT be called — unresolved template was detected
        await _boardClient.DidNotReceiveWithAnyArgs().SetFieldAsync(default!, default!, default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_ResolvedTemplate_CallsSetField()
    {
        // Context contains "estimation" — template is fully resolved
        var context = new Dictionary<string, string> { ["estimation"] = "4" };
        var target = new TransitionTarget([new TransitionAction(ActionTypes.SetField, "{{estimation}}", Field: "Estimate")]);

        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None, context);

        await _boardClient.Received(1).SetFieldAsync(CardId, "Estimate", "4", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_UnresolvedTemplateInField_SkipsAction()
    {
        // Context does not contain "fieldName" — {{fieldName}} remains unresolved
        var context = new Dictionary<string, string> { ["other"] = "x" };
        var target = new TransitionTarget([new TransitionAction(ActionTypes.ClearField, Field: "{{fieldName}}")]);

        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None, context);

        await _boardClient.DidNotReceiveWithAnyArgs().ClearFieldAsync(default!, default!, default);
    }

    [Fact]
    public async Task ExecuteAsync_UnresolvedTemplateInValue_ContinuesToNextAction()
    {
        // Unresolved template should be skipped, but subsequent actions must still execute
        var context = new Dictionary<string, string> { ["other"] = "x" };
        var target = new TransitionTarget([
            new TransitionAction(ActionTypes.SetField, "{{estimation}}", Field: "Estimate"),
            new TransitionAction(ActionTypes.MoveToColumn, "Designed"),
        ]);

        await TransitionExecutor.ExecuteAsync(CardId, target, _boardClient, _logger, CancellationToken.None, context);

        // SetField was skipped, but MoveToColumn must still be called
        await _boardClient.DidNotReceiveWithAnyArgs().SetFieldAsync(default!, default!, default!, default);
        await _boardClient.Received(1).MoveCardToColumnAsync(CardId, "Designed", Arg.Any<CancellationToken>());
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

    // --- UpdateParentSum action ---

    [Fact]
    public async Task ExecuteAsync_UpdateParentSum_SumsChildEstimatesOnParent()
    {
        var crossRefResolver = Substitute.For<ICrossReferenceResolver>();

        // Card has a parent
        crossRefResolver.GetStructuredReferencesAsync(CardId, Arg.Any<CancellationToken>())
            .Returns([new CardReference("parent-1", "parent_item", null, "Story")]);

        // Parent has two children
        crossRefResolver.GetStructuredReferencesAsync("parent-1", Arg.Any<CancellationToken>())
            .Returns([
                new CardReference("child-a", "sub_item", null, "Task A"),
                new CardReference("child-b", "sub_item", null, "Task B"),
            ]);

        _boardClient.GetCardAsync("child-a", Arg.Any<CancellationToken>())
            .Returns(new BoardCard("child-a", "Task A", "", "Designed",
                Metadata: new Dictionary<string, string> { ["Estimate"] = "4" }));
        _boardClient.GetCardAsync("child-b", Arg.Any<CancellationToken>())
            .Returns(new BoardCard("child-b", "Task B", "", "Designed",
                Metadata: new Dictionary<string, string> { ["Estimate"] = "2" }));

        var target = new TransitionTarget([
            new TransitionAction(ActionTypes.UpdateParentSum, Field: "Estimate"),
        ]);

        await TransitionExecutor.ExecuteAsync(
            CardId, target, _boardClient, _logger, CancellationToken.None,
            crossRefResolver: crossRefResolver);

        await _boardClient.Received(1).SetFieldAsync("parent-1", "Estimate", "6", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_UpdateParentSum_NoParent_Noop()
    {
        var crossRefResolver = Substitute.For<ICrossReferenceResolver>();

        // Card has no parent
        crossRefResolver.GetStructuredReferencesAsync(CardId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<CardReference>());

        var target = new TransitionTarget([
            new TransitionAction(ActionTypes.UpdateParentSum, Field: "Estimate"),
        ]);

        await TransitionExecutor.ExecuteAsync(
            CardId, target, _boardClient, _logger, CancellationToken.None,
            crossRefResolver: crossRefResolver);

        await _boardClient.DidNotReceive().SetFieldAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_UpdateParentSum_NoCrossRefResolver_Noop()
    {
        var target = new TransitionTarget([
            new TransitionAction(ActionTypes.UpdateParentSum, Field: "Estimate"),
        ]);

        // No crossRefResolver passed — should not throw
        await TransitionExecutor.ExecuteAsync(
            CardId, target, _boardClient, _logger, CancellationToken.None,
            crossRefResolver: null);

        await _boardClient.DidNotReceive().SetFieldAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_UpdateParentSum_ChildrenWithoutEstimate_NoSetField()
    {
        var crossRefResolver = Substitute.For<ICrossReferenceResolver>();

        crossRefResolver.GetStructuredReferencesAsync(CardId, Arg.Any<CancellationToken>())
            .Returns([new CardReference("parent-1", "parent_item", null, "Story")]);
        crossRefResolver.GetStructuredReferencesAsync("parent-1", Arg.Any<CancellationToken>())
            .Returns([new CardReference("child-a", "sub_item", null, "Task A")]);

        _boardClient.GetCardAsync("child-a", Arg.Any<CancellationToken>())
            .Returns(new BoardCard("child-a", "Task A", "", "Backlog",
                Metadata: new Dictionary<string, string>())); // no Estimate

        var target = new TransitionTarget([
            new TransitionAction(ActionTypes.UpdateParentSum, Field: "Estimate"),
        ]);

        await TransitionExecutor.ExecuteAsync(
            CardId, target, _boardClient, _logger, CancellationToken.None,
            crossRefResolver: crossRefResolver);

        await _boardClient.DidNotReceive().SetFieldAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_UpdateParentSum_PartialEstimates_SumsAvailable()
    {
        var crossRefResolver = Substitute.For<ICrossReferenceResolver>();

        crossRefResolver.GetStructuredReferencesAsync(CardId, Arg.Any<CancellationToken>())
            .Returns([new CardReference("parent-1", "parent_item", null, "Story")]);
        crossRefResolver.GetStructuredReferencesAsync("parent-1", Arg.Any<CancellationToken>())
            .Returns([
                new CardReference("child-a", "sub_item", null, "Task A"),
                new CardReference("child-b", "sub_item", null, "Task B"),
            ]);

        _boardClient.GetCardAsync("child-a", Arg.Any<CancellationToken>())
            .Returns(new BoardCard("child-a", "Task A", "", "Designed",
                Metadata: new Dictionary<string, string> { ["Estimate"] = "8" }));
        _boardClient.GetCardAsync("child-b", Arg.Any<CancellationToken>())
            .Returns(new BoardCard("child-b", "Task B", "", "Backlog",
                Metadata: new Dictionary<string, string>())); // no estimate yet

        var target = new TransitionTarget([
            new TransitionAction(ActionTypes.UpdateParentSum, Field: "Estimate"),
        ]);

        await TransitionExecutor.ExecuteAsync(
            CardId, target, _boardClient, _logger, CancellationToken.None,
            crossRefResolver: crossRefResolver);

        // Only child-a has an estimate, so sum = 8
        await _boardClient.Received(1).SetFieldAsync("parent-1", "Estimate", "8", Arg.Any<CancellationToken>());
    }

    // --- CompleteParentIfReady action ---

    [Fact]
    public async Task ExecuteAsync_CompleteParentIfReady_AllChildrenDone_TransitionsParent()
    {
        var crossRefResolver = Substitute.For<ICrossReferenceResolver>();

        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Waiting for Tasks"] = new("Waiting for Tasks", null, "holding", null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("Done"),
                    }),
                ["Done"] = new("Done", null, "terminal", null, new()),
            },
            Roles: new());

        // Card has a parent
        crossRefResolver.GetStructuredReferencesAsync(CardId, Arg.Any<CancellationToken>())
            .Returns([new CardReference("parent-1", "parent_item", null, "Story")]);

        // Parent is in "Waiting for Tasks"
        _boardClient.GetCardAsync("parent-1", Arg.Any<CancellationToken>())
            .Returns(new BoardCard("parent-1", "Story", "", "Waiting for Tasks"));

        // Parent has two children, both in terminal state
        crossRefResolver.GetStructuredReferencesAsync("parent-1", Arg.Any<CancellationToken>())
            .Returns([
                new CardReference(CardId, "sub_item", null, "Task A"),
                new CardReference("child-b", "sub_item", null, "Task B"),
            ]);

        _boardClient.GetCardAsync(CardId, Arg.Any<CancellationToken>())
            .Returns(new BoardCard(CardId, "Task A", "", "Done"));
        _boardClient.GetCardAsync("child-b", Arg.Any<CancellationToken>())
            .Returns(new BoardCard("child-b", "Task B", "", "Done"));

        var target = new TransitionTarget([
            new TransitionAction(ActionTypes.CompleteParentIfReady),
        ]);

        await TransitionExecutor.ExecuteAsync(
            CardId, target, _boardClient, _logger, CancellationToken.None,
            crossRefResolver: crossRefResolver, workflowConfig: config);

        // Parent should be moved to Done
        await _boardClient.Received(1).MoveCardToColumnAsync("parent-1", "Done", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_CompleteParentIfReady_ChildrenPending_PostsProgressComment()
    {
        var crossRefResolver = Substitute.For<ICrossReferenceResolver>();

        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Waiting for Tasks"] = new("Waiting for Tasks", null, "holding", null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("Done"),
                    }),
                ["Done"] = new("Done", null, "terminal", null, new()),
            },
            Roles: new());

        crossRefResolver.GetStructuredReferencesAsync(CardId, Arg.Any<CancellationToken>())
            .Returns([new CardReference("parent-1", "parent_item", null, "Story")]);

        _boardClient.GetCardAsync("parent-1", Arg.Any<CancellationToken>())
            .Returns(new BoardCard("parent-1", "Story", "", "Waiting for Tasks"));

        crossRefResolver.GetStructuredReferencesAsync("parent-1", Arg.Any<CancellationToken>())
            .Returns([
                new CardReference(CardId, "sub_item", null, "Task A"),
                new CardReference("child-b", "sub_item", null, "Task B"),
            ]);

        _boardClient.GetCardAsync(CardId, Arg.Any<CancellationToken>())
            .Returns(new BoardCard(CardId, "Task A", "", "Done"));
        _boardClient.GetCardAsync("child-b", Arg.Any<CancellationToken>())
            .Returns(new BoardCard("child-b", "Task B", "", "Implementing")); // not done

        var target = new TransitionTarget([
            new TransitionAction(ActionTypes.CompleteParentIfReady),
        ]);

        await TransitionExecutor.ExecuteAsync(
            CardId, target, _boardClient, _logger, CancellationToken.None,
            crossRefResolver: crossRefResolver, workflowConfig: config);

        // Parent should NOT be moved
        await _boardClient.DidNotReceive().MoveCardToColumnAsync("parent-1", Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Progress comment should be posted
        await _boardClient.Received(1).UpsertAgentCommentAsync(
            "parent-1",
            Arg.Is<string>(s => s.Contains("1/2") && s.Contains("#child-b")),
            Arg.Is<string>(s => s.Contains("children-progress")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_CompleteParentIfReady_NoParent_Noop()
    {
        var crossRefResolver = Substitute.For<ICrossReferenceResolver>();
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Done"] = new("Done", null, "terminal", null, new()),
            },
            Roles: new());

        crossRefResolver.GetStructuredReferencesAsync(CardId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<CardReference>());

        var target = new TransitionTarget([
            new TransitionAction(ActionTypes.CompleteParentIfReady),
        ]);

        await TransitionExecutor.ExecuteAsync(
            CardId, target, _boardClient, _logger, CancellationToken.None,
            crossRefResolver: crossRefResolver, workflowConfig: config);

        await _boardClient.DidNotReceive().MoveCardToColumnAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_CompleteParentIfReady_NoCrossRefResolver_Noop()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Done"] = new("Done", null, "terminal", null, new()),
            },
            Roles: new());

        var target = new TransitionTarget([
            new TransitionAction(ActionTypes.CompleteParentIfReady),
        ]);

        await TransitionExecutor.ExecuteAsync(
            CardId, target, _boardClient, _logger, CancellationToken.None,
            crossRefResolver: null, workflowConfig: config);

        await _boardClient.DidNotReceive().MoveCardToColumnAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
