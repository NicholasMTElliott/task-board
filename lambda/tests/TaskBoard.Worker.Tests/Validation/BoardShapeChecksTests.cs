using TaskBoard.Worker.Models;
using TaskBoard.Worker.Validation;

namespace TaskBoard.Worker.Tests.Validation;

public class BoardShapeChecksTests
{
    private static WorkflowConfig MakeConfig(
        Dictionary<string, WorkflowState>? states = null,
        PollingConfig? polling = null,
        EstimationConfig? estimation = null,
        Dictionary<string, CardTypeDefinition>? cardTypes = null)
    {
        return new WorkflowConfig(
            States: states ?? new Dictionary<string, WorkflowState>
            {
                ["Ready"] = new("Ready", "ba", "agent_run", "p",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("Done"),
                    }),
                ["Done"] = new("Done", null, "terminal", null,
                    new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new("m", "s", ["S"]),
            },
            Polling: polling,
            Estimation: estimation,
            CardTypes: cardTypes);
    }

    private static BoardShape MakeShape(
        IEnumerable<string>? columns = null,
        IEnumerable<BoardField>? fields = null,
        IEnumerable<string>? labels = null)
    {
        return new BoardShape(
            (columns ?? new[] { "Ready", "Done" }).ToList(),
            (fields ?? Array.Empty<BoardField>()).ToList(),
            (labels ?? Array.Empty<string>()).ToList());
    }

    [Fact]
    public void StateColumn_NotOnBoard_ReportsError()
    {
        var cfg = MakeConfig();
        var shape = MakeShape(columns: new[] { "Ready", "Finished" }); // missing "Done"

        var findings = BoardShapeChecks.Check(cfg, shape);

        Assert.Contains(findings, f =>
            f.Severity == ValidationSeverity.Error
            && f.Path.Contains("states[Done]"));
    }

    [Fact]
    public void StateColumn_CaseMismatch_EmitsCaseHint()
    {
        var cfg = MakeConfig();
        var shape = MakeShape(columns: new[] { "ready", "done" });

        var findings = BoardShapeChecks.Check(cfg, shape);

        var ready = Assert.Single(findings, f => f.Path.Contains("states[Ready]"));
        Assert.Contains("case mismatch", ready.Hint ?? "");
    }

    [Fact]
    public void MoveToColumn_TargetNotOnBoard_ReportsError()
    {
        var cfg = MakeConfig();
        var shape = MakeShape(columns: new[] { "Ready" }); // Done missing

        var findings = BoardShapeChecks.Check(cfg, shape);

        Assert.Contains(findings, f =>
            f.Path.Contains("transitions.COMPLETE")
            && f.Message.Contains("moveToColumn"));
    }

    [Fact]
    public void PriorityField_Missing_ReportsError()
    {
        var cfg = MakeConfig(polling: new PollingConfig("priority", ["P0", "P1"]));
        var shape = MakeShape();

        var findings = BoardShapeChecks.Check(cfg, shape);

        Assert.Contains(findings, f => f.Path == "polling.priorityFieldName");
    }

    [Fact]
    public void PriorityOrder_ValueNotAnOption_ReportsError()
    {
        var cfg = MakeConfig(polling: new PollingConfig("priority", ["P0", "P1", "P99"]));
        var shape = MakeShape(fields: new[]
        {
            new BoardField("priority", "SINGLE_SELECT",
                new[] { new BoardFieldOption("P0", "a"), new BoardFieldOption("P1", "b") }),
        });

        var findings = BoardShapeChecks.Check(cfg, shape);

        Assert.Contains(findings, f =>
            f.Severity == ValidationSeverity.Error
            && f.Path == "polling.priorityOrder"
            && f.Message.Contains("P99"));
    }

    [Fact]
    public void PriorityField_FreeText_EmitsInfo()
    {
        var cfg = MakeConfig(polling: new PollingConfig("priority", ["P0"]));
        var shape = MakeShape(fields: new[]
        {
            new BoardField("priority", "TEXT", Options: null),
        });

        var findings = BoardShapeChecks.Check(cfg, shape);

        Assert.Contains(findings, f =>
            f.Severity == ValidationSeverity.Info && f.Path == "polling.priorityFieldName");
    }

    [Fact]
    public void EstimationField_Missing_ReportsError()
    {
        var cfg = MakeConfig(estimation: new EstimationConfig("1", 1, "Estimate", [1, 2, 4]));
        var shape = MakeShape();

        var findings = BoardShapeChecks.Check(cfg, shape);

        Assert.Contains(findings, f => f.Path == "estimation.fieldName");
    }

    [Fact]
    public void EstimationScale_NotAnOption_ReportsError()
    {
        var cfg = MakeConfig(estimation: new EstimationConfig("1", 1, "Estimate", [1, 2, 4, 99]));
        var shape = MakeShape(fields: new[]
        {
            new BoardField("Estimate", "SINGLE_SELECT",
                new[] { new BoardFieldOption("1", ""), new BoardFieldOption("2", ""), new BoardFieldOption("4", "") }),
        });

        var findings = BoardShapeChecks.Check(cfg, shape);

        Assert.Contains(findings, f =>
            f.Path == "estimation.scale" && f.Message.Contains("99"));
    }

    [Fact]
    public void SetField_UnknownField_ReportsError()
    {
        var cfg = MakeConfig(states: new Dictionary<string, WorkflowState>
        {
            ["Ready"] = new("Ready", "ba", "agent_run", "p",
                new Dictionary<string, TransitionTarget>
                {
                    ["COMPLETE"] = new([
                        new TransitionAction(ActionTypes.MoveToColumn, "Done"),
                        new TransitionAction(ActionTypes.SetField, "1", "NoSuchField"),
                    ]),
                }),
            ["Done"] = new("Done", null, "terminal", null, new Dictionary<string, TransitionTarget>()),
        });
        var shape = MakeShape();

        var findings = BoardShapeChecks.Check(cfg, shape);

        Assert.Contains(findings, f =>
            f.Path.Contains("field") && f.Message.Contains("NoSuchField"));
    }

    [Fact]
    public void SetField_TemplatedValue_IsNotCheckedAgainstOptions()
    {
        var cfg = MakeConfig(states: new Dictionary<string, WorkflowState>
        {
            ["Ready"] = new("Ready", "ba", "agent_run", "p",
                new Dictionary<string, TransitionTarget>
                {
                    ["COMPLETE"] = new([
                        new TransitionAction(ActionTypes.MoveToColumn, "Done"),
                        new TransitionAction(ActionTypes.SetField, "{{estimation}}", "Estimate"),
                    ]),
                }),
            ["Done"] = new("Done", null, "terminal", null, new Dictionary<string, TransitionTarget>()),
        });
        var shape = MakeShape(fields: new[]
        {
            new BoardField("Estimate", "SINGLE_SELECT",
                new[] { new BoardFieldOption("1", ""), new BoardFieldOption("2", "") }),
        });

        var findings = BoardShapeChecks.Check(cfg, shape);

        Assert.DoesNotContain(findings, f => f.Message.Contains("{{estimation}}"));
    }

    [Fact]
    public void FieldFilter_UnknownField_ReportsError()
    {
        var cfg = MakeConfig(states: new Dictionary<string, WorkflowState>
        {
            ["Ready"] = new("Ready", "ba", "agent_run", "p",
                new Dictionary<string, TransitionTarget>(),
                Filters: [new CardFilter(FilterTypes.Field, FilterOperators.Equals, "x", "Nope")]),
            ["Done"] = new("Done", null, "terminal", null, new Dictionary<string, TransitionTarget>()),
        });
        var shape = MakeShape();

        var findings = BoardShapeChecks.Check(cfg, shape);

        Assert.Contains(findings, f => f.Path.Contains("filters[0].field") && f.Message.Contains("Nope"));
    }

    [Fact]
    public void CardTypeLabel_Missing_EmitsWarning_NotError()
    {
        var cfg = MakeConfig(cardTypes: new Dictionary<string, CardTypeDefinition>
        {
            ["story"] = new("Story", "type", []),
        });
        var shape = MakeShape(labels: new[] { "type:task" });

        var findings = BoardShapeChecks.Check(cfg, shape);

        Assert.Contains(findings, f =>
            f.Severity == ValidationSeverity.Warning
            && f.Message.Contains("type:story"));
    }

    [Fact]
    public void HappyPath_AllClean_NoFindings()
    {
        var cfg = MakeConfig();
        var shape = MakeShape();

        var findings = BoardShapeChecks.Check(cfg, shape);

        Assert.Empty(findings);
    }
}
