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
    public void CardTypeLabel_EmptyLabelPrefix_SkipsLabelCheck()
    {
        // When labelPrefix is empty, the type opts out of labels — no warning should be emitted.
        var cfg = MakeConfig(cardTypes: new Dictionary<string, CardTypeDefinition>
        {
            ["story"] = new("Story", LabelPrefix: null, AllowedChildren: []),
        });
        var shape = MakeShape(labels: new[] { "something-else" });

        var findings = BoardShapeChecks.Check(cfg, shape);

        Assert.DoesNotContain(findings, f => f.Message.Contains(":story"));
    }

    [Fact]
    public void CardTypeField_Missing_ReportsError()
    {
        var cfg = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
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
            CardTypes: new Dictionary<string, CardTypeDefinition>
            {
                ["story"] = new("User Story", LabelPrefix: null, AllowedChildren: []),
            },
            CardTypeField: "Type");
        var shape = MakeShape(); // no fields

        var findings = BoardShapeChecks.Check(cfg, shape);

        Assert.Contains(findings, f =>
            f.Severity == ValidationSeverity.Error && f.Path == "cardTypeField");
    }

    [Fact]
    public void CardTypeField_TypeNameNotAnOption_ReportsError()
    {
        var cfg = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
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
            CardTypes: new Dictionary<string, CardTypeDefinition>
            {
                ["task"] = new("Chore", LabelPrefix: null, AllowedChildren: []), // "Chore" not an option
            },
            CardTypeField: "Type");
        var typeField = new BoardField("Type", "ProjectV2SingleSelectField",
            new List<BoardFieldOption> { new("Task", "o1"), new("Story", "o2") });
        var shape = MakeShape(fields: new[] { typeField });

        var findings = BoardShapeChecks.Check(cfg, shape);

        Assert.Contains(findings, f =>
            f.Severity == ValidationSeverity.Error
            && f.Path.Contains("cardTypes[task]")
            && f.Message.Contains("Chore"));
    }

    [Fact]
    public void GenerationConfig_SetFields_UnknownField_ReportsError()
    {
        var step = new WorkflowStep("gen", "ba", TaskPrompt: "p",
            GenerationConfig: new GenerationConfig(
                TargetType: "task",
                TargetColumn: "Ready",
                SetFields: new Dictionary<string, string> { ["Nope"] = "X" }));
        var cfg = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready"] = new("Ready", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("Ready"),
                    },
                    Steps: [step]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new("m", "s", ["S"]),
            });
        var shape = MakeShape(columns: new[] { "Ready" });

        var findings = BoardShapeChecks.Check(cfg, shape);

        Assert.Contains(findings, f =>
            f.Severity == ValidationSeverity.Error
            && f.Path.Contains("setFields")
            && f.Message.Contains("Nope"));
    }

    [Fact]
    public void GenerationConfig_SetFields_ValueNotAnOption_ReportsError()
    {
        var step = new WorkflowStep("gen", "ba", TaskPrompt: "p",
            GenerationConfig: new GenerationConfig(
                TargetType: "task",
                TargetColumn: "Ready",
                SetFields: new Dictionary<string, string> { ["Activity"] = "BadValue" }));
        var cfg = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready"] = new("Ready", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("Ready"),
                    },
                    Steps: [step]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new("m", "s", ["S"]),
            });
        var actField = new BoardField("Activity", "ProjectV2SingleSelectField",
            new List<BoardFieldOption> { new("Design", "o1"), new("Test", "o2") });
        var shape = MakeShape(columns: new[] { "Ready" }, fields: new[] { actField });

        var findings = BoardShapeChecks.Check(cfg, shape);

        Assert.Contains(findings, f =>
            f.Severity == ValidationSeverity.Error
            && f.Path.Contains("setFields[Activity]")
            && f.Message.Contains("BadValue"));
    }

    [Fact]
    public void GenerationConfig_SetFields_TemplatedValue_Skipped()
    {
        var step = new WorkflowStep("gen", "ba", TaskPrompt: "p",
            GenerationConfig: new GenerationConfig(
                TargetType: "task",
                TargetColumn: "Ready",
                SetFields: new Dictionary<string, string> { ["Activity"] = "{{estimation}}" }));
        var cfg = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready"] = new("Ready", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("Ready"),
                    },
                    Steps: [step]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new("m", "s", ["S"]),
            });
        var actField = new BoardField("Activity", "ProjectV2SingleSelectField",
            new List<BoardFieldOption> { new("Design", "o1") });
        var shape = MakeShape(columns: new[] { "Ready" }, fields: new[] { actField });

        var findings = BoardShapeChecks.Check(cfg, shape);

        Assert.DoesNotContain(findings, f => f.Path.Contains("setFields"));
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
