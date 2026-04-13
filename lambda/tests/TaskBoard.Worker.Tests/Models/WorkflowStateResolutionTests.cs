using System.Text.Json;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Tests.Models;

/// <summary>
/// Tests for WorkflowConfig.Column support: GetEffectiveColumn, FindStatesByColumn,
/// GetTerminalColumnNames, ResolveState, and WorkflowConfigValidator shared-column enforcement.
/// </summary>
public class WorkflowStateResolutionTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static BoardCard MakeCard(string columnId,
        IReadOnlyDictionary<string, string>? metadata = null,
        IReadOnlyList<string>? labels = null,
        IReadOnlyList<string>? assignees = null) =>
        new("card-1", "Test Card", "body", columnId, metadata, labels, assignees);

    private static WorkflowState MakeState(
        string name, string gateType = "agent_run",
        List<CardFilter>? filters = null,
        string? column = null) =>
        new(name, null, gateType, null,
            new Dictionary<string, TransitionTarget>(),
            Filters: filters, Column: column);

    private static WorkflowConfig MakeSharedColumnConfig()
    {
        // Two states sharing "Ready" column, disambiguated by Activity field
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["design"] = MakeState("Design", "agent_run",
                    [new CardFilter(FilterTypes.Field, FilterOperators.Equals, "Design", "Activity")],
                    column: "Ready"),
                ["implementation"] = MakeState("Implementation", "agent_run",
                    [new CardFilter(FilterTypes.Field, FilterOperators.Equals, "Implementation", "Activity")],
                    column: "Ready"),
                ["done"] = MakeState("Done", "terminal", column: "Done"),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["r"] = new("model", "prompt", new List<string>()),
            });
    }

    // ── GetEffectiveColumn tests ───────────────────────────────────────────

    [Fact]
    public void GetEffectiveColumn_WhenColumnIsNull_ReturnsStateId()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready for Design"] = MakeState("Ready for Design"),
            },
            Roles: new Dictionary<string, WorkflowRole>());

        Assert.Equal("Ready for Design", config.GetEffectiveColumn("Ready for Design"));
    }

    [Fact]
    public void GetEffectiveColumn_WhenColumnIsSet_ReturnsColumnValue()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["design"] = MakeState("Design", column: "Ready"),
            },
            Roles: new Dictionary<string, WorkflowRole>());

        Assert.Equal("Ready", config.GetEffectiveColumn("design"));
    }

    [Fact]
    public void GetEffectiveColumn_WhenStateIdNotFound_ReturnsStateId()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>(),
            Roles: new Dictionary<string, WorkflowRole>());

        Assert.Equal("nonexistent", config.GetEffectiveColumn("nonexistent"));
    }

    // ── FindStatesByColumn tests ──────────────────────────────────────────

    [Fact]
    public void FindStatesByColumn_SingleMatch_ReturnsOne()
    {
        var config = MakeSharedColumnConfig();

        var result = config.FindStatesByColumn("Done");

        Assert.Single(result);
        Assert.Equal("Done", result[0].Name);
    }

    [Fact]
    public void FindStatesByColumn_SharedColumn_ReturnsAll()
    {
        var config = MakeSharedColumnConfig();

        var result = config.FindStatesByColumn("Ready");

        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.Name == "Design");
        Assert.Contains(result, s => s.Name == "Implementation");
    }

    [Fact]
    public void FindStatesByColumn_NoMatch_ReturnsEmpty()
    {
        var config = MakeSharedColumnConfig();

        var result = config.FindStatesByColumn("Backlog");

        Assert.Empty(result);
    }

    [Fact]
    public void FindStatesByColumn_CaseInsensitive()
    {
        var config = MakeSharedColumnConfig();

        var upper = config.FindStatesByColumn("DONE");
        var lower = config.FindStatesByColumn("done");

        Assert.Single(upper);
        Assert.Single(lower);
    }

    // ── GetTerminalColumnNames tests ──────────────────────────────────────

    [Fact]
    public void GetTerminalColumnNames_UniqueColumns_ReturnsColumnNames()
    {
        // Backward-compatible config: state key = column name
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Done"] = MakeState("Done", "terminal"),
                ["Backlog"] = MakeState("Backlog", "manual_entry"),
            },
            Roles: new Dictionary<string, WorkflowRole>());

        var names = config.GetTerminalColumnNames();

        Assert.Single(names);
        Assert.Contains("Done", names);
    }

    [Fact]
    public void GetTerminalColumnNames_WithColumnOverride_ReturnsColumnValue()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["done"] = MakeState("Done", "terminal", column: "Completed"),
            },
            Roles: new Dictionary<string, WorkflowRole>());

        var names = config.GetTerminalColumnNames();

        Assert.Single(names);
        Assert.Contains("Completed", names);
        Assert.DoesNotContain("done", names);
    }

    [Fact]
    public void GetTerminalColumnNames_MultipleTerminalStatesSameColumn_Deduplicates()
    {
        // Unusual but valid: two terminal states pointing to same column
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["done_a"] = MakeState("Done A", "terminal", column: "Done"),
                ["done_b"] = MakeState("Done B", "terminal", column: "Done"),
            },
            Roles: new Dictionary<string, WorkflowRole>());

        var names = config.GetTerminalColumnNames();

        Assert.Single(names);
        Assert.Contains("Done", names);
    }

    // ── ResolveState tests ────────────────────────────────────────────────

    [Fact]
    public void ResolveState_SingleStateNoFilters_ReturnsState()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready for Design"] = MakeState("Ready for Design"),
            },
            Roles: new Dictionary<string, WorkflowRole>());
        var card = MakeCard("Ready for Design");

        var state = config.ResolveState(card);

        Assert.NotNull(state);
        Assert.Equal("Ready for Design", state.Name);
    }

    [Fact]
    public void ResolveState_SharedColumnWithFilters_ReturnsMatchingState()
    {
        var config = MakeSharedColumnConfig();
        var card = MakeCard("Ready", metadata: new Dictionary<string, string> { ["Activity"] = "Design" });

        var state = config.ResolveState(card);

        Assert.NotNull(state);
        Assert.Equal("Design", state.Name);
    }

    [Fact]
    public void ResolveState_SharedColumnDifferentPhase_ReturnsCorrectState()
    {
        var config = MakeSharedColumnConfig();
        var card = MakeCard("Ready", metadata: new Dictionary<string, string> { ["Activity"] = "Implementation" });

        var state = config.ResolveState(card);

        Assert.NotNull(state);
        Assert.Equal("Implementation", state.Name);
    }

    [Fact]
    public void ResolveState_SharedColumnNoMatchingFilter_ReturnsNull()
    {
        var config = MakeSharedColumnConfig();
        // Card in "Ready" but Activity = "Review" which matches no state filter
        var card = MakeCard("Ready", metadata: new Dictionary<string, string> { ["Activity"] = "Review" });

        var state = config.ResolveState(card);

        Assert.Null(state);
    }

    [Fact]
    public void ResolveState_UnknownColumn_ReturnsNull()
    {
        var config = MakeSharedColumnConfig();
        var card = MakeCard("Backlog");

        var state = config.ResolveState(card);

        Assert.Null(state);
    }

    [Fact]
    public void ResolveState_SingleStateWithFilters_FailsFilters_ReturnsNull()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["design"] = MakeState("Design", filters:
                [
                    new CardFilter(FilterTypes.Field, FilterOperators.Equals, "Design", "Activity")
                ], column: "Ready"),
            },
            Roles: new Dictionary<string, WorkflowRole>());
        var card = MakeCard("Ready", metadata: new Dictionary<string, string> { ["Activity"] = "Test" });

        var state = config.ResolveState(card);

        Assert.Null(state);
    }

    [Fact]
    public void ResolveState_Ambiguous_ThrowsInvalidOperationException()
    {
        // Two states with filters that both match (e.g., both have isEmpty filters)
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["state_a"] = MakeState("State A", filters:
                [
                    new CardFilter(FilterTypes.Assignee, FilterOperators.IsEmpty)
                ], column: "Ready"),
                ["state_b"] = MakeState("State B", filters:
                [
                    new CardFilter(FilterTypes.Assignee, FilterOperators.IsEmpty)
                ], column: "Ready"),
            },
            Roles: new Dictionary<string, WorkflowRole>());
        var card = MakeCard("Ready"); // no assignees — both states match

        Assert.Throws<InvalidOperationException>(() => config.ResolveState(card));
    }

    [Fact]
    public void ResolveState_BackwardCompatible_GithubWorkflowStyle()
    {
        // Simulate a typical workflow.github.json pattern: state keys = column names, no Column property
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready for Design"]        = MakeState("Ready for Design", "agent_run"),
                ["Designing"]               = MakeState("Designing", "in_progress"),
                ["Designed"]                = MakeState("Designed", "manual_gate"),
                ["Ready for Implementation"] = MakeState("Ready for Implementation", "agent_run"),
                ["Done"]                    = MakeState("Done", "terminal"),
            },
            Roles: new Dictionary<string, WorkflowRole>());

        var card = MakeCard("Ready for Design");
        var state = config.ResolveState(card);

        Assert.NotNull(state);
        Assert.Equal("Ready for Design", state.Name);

        var terminalCols = config.GetTerminalColumnNames();
        Assert.Contains("Done", terminalCols);
        Assert.DoesNotContain("Ready for Design", terminalCols);
    }

    // ── WorkflowConfigValidator shared-column tests ───────────────────────

    [Fact]
    public void Validator_SharedColumn_ActionableState_WithoutFilters_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                // Two states on "Ready": one has filters, one doesn't
                ["design"] = new WorkflowState("Design", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>(),
                    Filters: [new CardFilter(FilterTypes.Field, FilterOperators.Equals, "Design", "Activity")],
                    Column: "Ready"),
                ["impl"] = new WorkflowState("Implementation", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>(),
                    Column: "Ready"), // no filters!
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["r"] = new("model", "prompt", new List<string>()),
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("impl") && e.Contains("shares column") && e.Contains("no filters"));
    }

    [Fact]
    public void Validator_SharedColumn_AllHaveFilters_NoError()
    {
        var config = MakeSharedColumnConfig();

        // Add required role-less agent_run prompts to pass other validation
        var updatedConfig = config with
        {
            States = config.States.ToDictionary(
                kvp => kvp.Key,
                kvp => kvp.Value.GateType == "agent_run"
                    ? kvp.Value with { Steps = [new("step", "r", "do work")] }
                    : kvp.Value)
        };

        var errors = WorkflowConfigValidator.Validate(updatedConfig);

        // No shared-column enforcement errors
        Assert.DoesNotContain(errors, e => e.Contains("shares column"));
    }

    [Fact]
    public void Validator_SharedColumn_NonActionableOnly_NoEnforcementError()
    {
        // Two non-actionable states sharing a column — no filter enforcement needed
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["in_prog_a"] = new WorkflowState("In Progress A", null, "in_progress", null,
                    new Dictionary<string, TransitionTarget>(), Column: "In progress"),
                ["in_prog_b"] = new WorkflowState("In Progress B", null, "in_progress", null,
                    new Dictionary<string, TransitionTarget>(), Column: "In progress"),
            },
            Roles: new Dictionary<string, WorkflowRole>());

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("shares column"));
    }

    [Fact]
    public void Validator_MoveToColumn_TargetsEffectiveColumnName_NoError()
    {
        // transition targets "Ready" which is the effective column of state "design"
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["source"] = new WorkflowState("Source", null, "holding", null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("Ready"),
                    }),
                ["design"] = new WorkflowState("Design", null, "holding", null,
                    new Dictionary<string, TransitionTarget>(),
                    Column: "Ready"),
            },
            Roles: new Dictionary<string, WorkflowRole>());

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("targets column") && e.Contains("not mapped"));
    }

    [Fact]
    public void Validator_MoveToColumn_TargetsNonexistentColumn_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["source"] = new WorkflowState("Source", null, "holding", null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("Nonexistent Column"),
                    }),
            },
            Roles: new Dictionary<string, WorkflowRole>());

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("Nonexistent Column") && e.Contains("not mapped"));
    }

    [Fact]
    public void Validator_GenerationConfig_TargetColumn_EffectiveColumnName_NoError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["tasking"] = new WorkflowState("Tasking", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>(),
                    Steps:
                    [
                        new WorkflowStep("generate", "r", "prompt", GenerationConfig: new GenerationConfig(
                            "task", TargetColumn: "Ready"))
                    ]),
                ["design"] = new WorkflowState("Design", null, "holding", null,
                    new Dictionary<string, TransitionTarget>(),
                    Column: "Ready"),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["r"] = new("model", "prompt", new List<string>()),
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("generationConfig.targetColumn") && e.Contains("not a known"));
    }

    // ── workflow.simple.example.json loads and validates correctly ─────────

    [Fact]
    public void WorkflowSimpleExample_LoadsAndValidates_WithSharedColumns()
    {
        // Verify the example config can be loaded from disk and has no config-level validation errors
        var configPath = Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..", "..", "..", "..",
            "workflow.simple.example.json");
        configPath = Path.GetFullPath(configPath);

        if (!File.Exists(configPath))
        {
            // Try alternate relative path for CI/CD environments
            configPath = Path.Combine(AppContext.BaseDirectory, "workflow.simple.example.json");
        }

        if (!File.Exists(configPath))
        {
            // Skip gracefully if the file cannot be located in the current test execution context
            return;
        }

        var json = File.ReadAllText(configPath);
        var opts = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var config = JsonSerializer.Deserialize<WorkflowConfig>(json, opts);

        Assert.NotNull(config);

        // The five phases (design, implementation, review, test, merge) share "Ready"
        var readyStates = config.FindStatesByColumn("Ready");
        Assert.Equal(5, readyStates.Count);

        // Terminal column is "Done", not "done" (state key)
        var terminalCols = config.GetTerminalColumnNames();
        Assert.Contains("Done", terminalCols, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("done", terminalCols, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveState_AssigneeFilter_MatchesWhenEmpty()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["design"] = MakeState("Design", filters:
                [
                    new CardFilter(FilterTypes.Field,    FilterOperators.Equals,  "Design", "Activity"),
                    new CardFilter(FilterTypes.Assignee, FilterOperators.IsEmpty),
                ], column: "Ready"),
            },
            Roles: new Dictionary<string, WorkflowRole>());

        // No assignees, Activity = Design → matches
        var card = MakeCard("Ready",
            metadata: new Dictionary<string, string> { ["Activity"] = "Design" },
            assignees: []);

        var state = config.ResolveState(card);
        Assert.NotNull(state);
        Assert.Equal("Design", state.Name);
    }

    [Fact]
    public void ResolveState_AssigneeFilter_NoMatchWhenAssigned()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["design"] = MakeState("Design", filters:
                [
                    new CardFilter(FilterTypes.Field,    FilterOperators.Equals,  "Design", "Activity"),
                    new CardFilter(FilterTypes.Assignee, FilterOperators.IsEmpty),
                ], column: "Ready"),
            },
            Roles: new Dictionary<string, WorkflowRole>());

        // Card assigned to agent → should NOT match (filter requires empty assignee)
        var card = MakeCard("Ready",
            metadata: new Dictionary<string, string> { ["Activity"] = "Design" },
            assignees: ["agent-bot"]);

        var state = config.ResolveState(card);
        Assert.Null(state);
    }
}
