using TaskBoard.Worker.Models;
using TaskBoard.Worker.Validation;

namespace TaskBoard.Worker.Tests.Validation;

/// <summary>
/// Pins <see cref="BoardScaffoldDiff"/>: walks WorkflowConfig to discover
/// fields/options/labels/columns the workflow uses, then diffs against an
/// introspected <see cref="BoardShape"/>.
/// </summary>
public class BoardScaffoldDiffTests
{
    private static WorkflowConfig MinimalExampleShape() => new(
        States: new()
        {
            ["design"] = new WorkflowState(
                Name: "Design",
                Role: null,
                GateType: GateTypes.AgentRun,
                TaskPrompt: null,
                Transitions: new()
                {
                    [TransitionKeys.Complete] = new TransitionTarget([
                        new TransitionAction(ActionTypes.Unassign),
                        new TransitionAction(ActionTypes.SetField, Field: "Activity", Value: "Implementation"),
                        new TransitionAction(ActionTypes.MoveToColumn, Value: "Ready"),
                    ]),
                },
                Column: "Ready",
                Filters:
                [
                    new CardFilter(FilterTypes.Field, FilterOperators.Equals, Field: "Activity", Value: "Design"),
                    new CardFilter(FilterTypes.Assignee, FilterOperators.IsEmpty),
                ]),
            ["done"] = new WorkflowState(
                Name: "Done",
                Role: null,
                GateType: GateTypes.Terminal,
                TaskPrompt: null,
                Transitions: new(),
                Column: "Done"),
        },
        Roles: new(),
        Polling: new PollingConfig(
            PriorityFieldName: "priority",
            PriorityOrder: ["Urgent", "Critical", "Important", "Desired"]),
        CardTypes: new()
        {
            ["story"] = new CardTypeDefinition("User Story", LabelPrefix: "type", AllowedChildren: ["task"]),
            ["task"]  = new CardTypeDefinition("Task", LabelPrefix: "type"),
        });

    [Fact]
    public void BoardAlreadyMatches_PlanIsEmpty()
    {
        var config = MinimalExampleShape();
        var shape = new BoardShape(
            ColumnNames: ["Ready", "Done"],
            Fields:
            [
                new BoardField("Activity", "SINGLE_SELECT",
                [
                    new BoardFieldOption("Design", "opt-d"),
                    new BoardFieldOption("Implementation", "opt-i"),
                ]),
                new BoardField("priority", "SINGLE_SELECT",
                [
                    new BoardFieldOption("Urgent", "opt-u"),
                    new BoardFieldOption("Critical", "opt-c"),
                    new BoardFieldOption("Important", "opt-im"),
                    new BoardFieldOption("Desired", "opt-de"),
                ]),
            ],
            Labels: ["type:story", "type:task"]);

        var plan = BoardScaffoldDiff.Compute(config, shape);

        Assert.True(plan.IsEmpty);
        Assert.Empty(plan.MissingFields);
        Assert.Empty(plan.MissingLabels);
        Assert.Empty(plan.FieldOptionGaps);
        Assert.Empty(plan.MissingColumns);
    }

    [Fact]
    public void EmptyBoard_PlanContainsEverythingNew()
    {
        var config = MinimalExampleShape();
        var shape = new BoardShape(
            ColumnNames: ["Ready", "Done"],   // columns at least exist so we don't get column noise
            Fields: [],
            Labels: []);

        var plan = BoardScaffoldDiff.Compute(config, shape);

        Assert.False(plan.IsEmpty);
        Assert.Equal(2, plan.MissingFields.Count);
        var activity = plan.MissingFields.Single(f => f.Name == "Activity");
        Assert.Contains("Design", activity.Options);
        Assert.Contains("Implementation", activity.Options);

        var priority = plan.MissingFields.Single(f => f.Name == "priority");
        Assert.Contains("Urgent", priority.Options);
        Assert.Contains("Desired", priority.Options);

        Assert.Equal(2, plan.MissingLabels.Count);
        Assert.Contains("type:story", plan.MissingLabels);
        Assert.Contains("type:task", plan.MissingLabels);
    }

    [Fact]
    public void FieldExistsButMissingOptions_GapNotMissingField()
    {
        var config = MinimalExampleShape();
        var shape = new BoardShape(
            ColumnNames: ["Ready", "Done"],
            Fields:
            [
                new BoardField("Activity", "SINGLE_SELECT",
                [
                    new BoardFieldOption("Design", "opt-d"),
                    // Implementation missing
                ]),
                new BoardField("priority", "SINGLE_SELECT",
                [
                    new BoardFieldOption("Urgent", "opt-u"),
                    new BoardFieldOption("Critical", "opt-c"),
                    new BoardFieldOption("Important", "opt-im"),
                    new BoardFieldOption("Desired", "opt-de"),
                ]),
            ],
            Labels: ["type:story", "type:task"]);

        var plan = BoardScaffoldDiff.Compute(config, shape);

        Assert.Empty(plan.MissingFields);
        var gap = Assert.Single(plan.FieldOptionGaps);
        Assert.Equal("Activity", gap.FieldName);
        Assert.Equal(["Implementation"], gap.MissingOptions);
        Assert.Empty(plan.MissingLabels);
    }

    [Fact]
    public void TemplateValuesIgnored_NotTreatedAsRequiredOptions()
    {
        // setField with value "{{estimation}}" should not contribute a literal
        // option requirement.
        var config = new WorkflowConfig(
            States: new()
            {
                ["design"] = new WorkflowState(
                    Name: "Design",
                    Role: null,
                    GateType: GateTypes.AgentRun,
                    TaskPrompt: null,
                    Transitions: new()
                    {
                        [TransitionKeys.Complete] = new TransitionTarget([
                            new TransitionAction(ActionTypes.SetField, Field: "Estimate", Value: "{{estimation}}"),
                            new TransitionAction(ActionTypes.MoveToColumn, Value: "Done"),
                        ]),
                    },
                    Column: "Ready"),
            },
            Roles: new());
        var shape = new BoardShape(
            ColumnNames: ["Ready", "Done"],
            Fields: [],
            Labels: []);

        var plan = BoardScaffoldDiff.Compute(config, shape);

        // Estimate field should be flagged as missing — but with no option values
        // (since the only setField value was a template).
        var estimate = plan.MissingFields.SingleOrDefault(f => f.Name == "Estimate");
        Assert.NotNull(estimate);
        Assert.Empty(estimate!.Options);
    }

    [Fact]
    public void EstimationConfig_AddsScaleOptionsToField()
    {
        var config = MinimalExampleShape() with
        {
            Estimation = new EstimationConfig(
                CalibrationTicketId: "1",
                CalibrationSize: 1,
                FieldName: "Estimate",
                Scale: [1, 2, 4, 8]),
        };
        var shape = new BoardShape(["Ready", "Done"], [], []);

        var plan = BoardScaffoldDiff.Compute(config, shape);

        var estimate = plan.MissingFields.SingleOrDefault(f => f.Name == "Estimate");
        Assert.NotNull(estimate);
        Assert.Equal(["1", "2", "4", "8"], estimate!.Options);
    }

    [Fact]
    public void CardTypeField_BecomesRequiredFieldWithTypeNamesAsOptions()
    {
        var config = MinimalExampleShape() with { CardTypeField = "Type" };
        var shape = new BoardShape(["Ready", "Done"], [], []);

        var plan = BoardScaffoldDiff.Compute(config, shape);

        var typeField = plan.MissingFields.Single(f => f.Name == "Type");
        Assert.Contains("User Story", typeField.Options);
        Assert.Contains("Task", typeField.Options);
    }

    [Fact]
    public void EmptyLabelPrefix_OptsOutOfLabel()
    {
        var config = new WorkflowConfig(
            States: new(),
            Roles: new(),
            CardTypes: new()
            {
                ["story"] = new CardTypeDefinition("User Story", LabelPrefix: "type"),
                ["task"]  = new CardTypeDefinition("Task", LabelPrefix: ""),  // opt-out
            });
        var shape = new BoardShape([], [], []);

        var plan = BoardScaffoldDiff.Compute(config, shape);

        Assert.Contains("type:story", plan.MissingLabels);
        Assert.DoesNotContain("type:task", plan.MissingLabels);
    }

    [Fact]
    public void MissingColumn_FlaggedAsManualAction()
    {
        // Workflow references column 'Ready' but board only has 'Done'.
        var config = MinimalExampleShape();
        var shape = new BoardShape(
            ColumnNames: ["Done"],
            Fields: [],
            Labels: []);

        var plan = BoardScaffoldDiff.Compute(config, shape);

        Assert.Contains("Ready", plan.MissingColumns);
        Assert.True(plan.HasManualActions);
    }

    [Fact]
    public void EmptyColumnList_SuppressesColumnNoise()
    {
        // The probe couldn't see the Status field (gh permissions). Don't
        // flag every workflow column as "missing" — that would be noise.
        var config = MinimalExampleShape();
        var shape = new BoardShape(
            ColumnNames: [],   // probe returned no columns
            Fields: [],
            Labels: []);

        var plan = BoardScaffoldDiff.Compute(config, shape);

        Assert.Empty(plan.MissingColumns);
    }

    [Fact]
    public void AddLabelAndRemoveLabelActions_BothContributeRequiredLabels()
    {
        // Symmetry guard: a workflow that does `removeLabel needs-triage`
        // requires that label to exist on the repo (gh fails on unknown labels),
        // just like a workflow that does `addLabel ready-for-review`.
        var config = new WorkflowConfig(
            States: new()
            {
                ["s"] = new WorkflowState(
                    Name: "S",
                    Role: null,
                    GateType: GateTypes.AgentRun,
                    TaskPrompt: null,
                    Transitions: new()
                    {
                        [TransitionKeys.InProgress] = new TransitionTarget([
                            new TransitionAction(ActionTypes.AddLabel, Value: "in-progress"),
                            new TransitionAction(ActionTypes.RemoveLabel, Value: "needs-triage"),
                        ]),
                        [TransitionKeys.Complete] = new TransitionTarget([
                            new TransitionAction(ActionTypes.RemoveLabel, Value: "in-progress"),
                            new TransitionAction(ActionTypes.AddLabel, Value: "ready-for-review"),
                        ]),
                    },
                    Column: "Ready"),
            },
            Roles: new());
        var shape = new BoardShape(["Ready"], [], []);

        var plan = BoardScaffoldDiff.Compute(config, shape);

        Assert.Contains("needs-triage", plan.MissingLabels);
        Assert.Contains("in-progress", plan.MissingLabels);
        Assert.Contains("ready-for-review", plan.MissingLabels);
    }

    [Fact]
    public void IsEmpty_AndHasFlags_ReflectPlanContents()
    {
        var emptyPlan = new BoardScaffoldPlan([], [], [], []);
        Assert.True(emptyPlan.IsEmpty);
        Assert.False(emptyPlan.HasCreatableActions);
        Assert.False(emptyPlan.HasManualActions);

        var fieldsOnly = new BoardScaffoldPlan(
            [new MissingField("F", ["A"])], [], [], []);
        Assert.False(fieldsOnly.IsEmpty);
        Assert.True(fieldsOnly.HasCreatableActions);
        Assert.False(fieldsOnly.HasManualActions);

        var manualOnly = new BoardScaffoldPlan(
            [], [new FieldOptionGap("F", ["A"], ["B"])], [], []);
        Assert.False(manualOnly.IsEmpty);
        Assert.False(manualOnly.HasCreatableActions);
        Assert.True(manualOnly.HasManualActions);
    }
}
