using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using TaskBoard.Worker.Validation;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Pins <see cref="ScaffoldBoardRunner"/>: plan-then-apply UX, idempotency
/// guards, and applier wiring.
/// </summary>
public class ScaffoldBoardRunnerTests
{
    /// <summary>Stub probe that returns a canned shape (or null/throw).</summary>
    private sealed class CannedProbe(BoardShape? shape, Exception? toThrow = null) : IBoardShapeProbe
    {
        public Task<BoardShape?> ProbeAsync(string boardId, CancellationToken ct)
        {
            if (toThrow is not null) throw toThrow;
            return Task.FromResult(shape);
        }
    }

    /// <summary>Recording applier — captures every create call for assertion.</summary>
    private sealed class RecordingApplier : IBoardShapeApplier
    {
        public bool CanApply { get; init; } = true;
        public List<(string Field, IReadOnlyList<string> Options)> Fields { get; } = new();
        public List<string> Labels { get; } = new();
        public ApplyOutcome FieldOutcome { get; init; } = ApplyOutcome.Created;
        public ApplyOutcome LabelOutcome { get; init; } = ApplyOutcome.Created;

        public Task<ApplyResult> CreateSingleSelectFieldAsync(
            string boardId, string fieldName, IReadOnlyList<string> options, CancellationToken ct)
        {
            Fields.Add((fieldName, options));
            return Task.FromResult(new ApplyResult(FieldOutcome, $"create field '{fieldName}'"));
        }

        public Task<ApplyResult> CreateLabelAsync(string labelName, CancellationToken ct)
        {
            Labels.Add(labelName);
            return Task.FromResult(new ApplyResult(LabelOutcome, $"create label '{labelName}'"));
        }
    }

    private static WorkflowConfig MinimalExample() => new(
        States: new()
        {
            ["design"] = new WorkflowState(
                Name: "Design",
                Role: null,
                GateType: GateTypes.AgentRun,
                TaskPrompt: null,
                Transitions: new(),
                Column: "Ready",
                Filters:
                [
                    new CardFilter(FilterTypes.Field, FilterOperators.Equals, Field: "Activity", Value: "Design"),
                    new CardFilter(FilterTypes.Assignee, FilterOperators.IsEmpty),
                ]),
        },
        Roles: new(),
        Polling: new PollingConfig("priority", ["Urgent", "Desired"]),
        CardTypes: new()
        {
            ["task"] = new CardTypeDefinition("Task", LabelPrefix: "type"),
        });

    [Fact]
    public async Task BoardMatches_PlanIsEmpty_ReturnsZeroNoApplyNeeded()
    {
        var workflow = MinimalExample();
        var shape = new BoardShape(
            ColumnNames: ["Ready"],
            Fields:
            [
                new BoardField("Activity", "SINGLE_SELECT", [new BoardFieldOption("Design", "x")]),
                new BoardField("priority", "SINGLE_SELECT",
                [
                    new BoardFieldOption("Urgent", "x"),
                    new BoardFieldOption("Desired", "y"),
                ]),
            ],
            Labels: ["type:task"]);
        var applier = new RecordingApplier();
        var stdout = new StringWriter();
        var runner = new ScaffoldBoardRunner(
            workflow, new CannedProbe(shape), applier, NullLogger.Instance, stdout);

        var exit = await runner.RunAsync("123", apply: false, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("Nothing to do", stdout.ToString());
        Assert.Empty(applier.Fields);
        Assert.Empty(applier.Labels);
    }

    [Fact]
    public async Task DryRunByDefault_ListsActionsButDoesNotApply()
    {
        var workflow = MinimalExample();
        var shape = new BoardShape(["Ready"], [], []);   // empty board
        var applier = new RecordingApplier();
        var stdout = new StringWriter();
        var runner = new ScaffoldBoardRunner(
            workflow, new CannedProbe(shape), applier, NullLogger.Instance, stdout);

        var exit = await runner.RunAsync("123", apply: false, CancellationToken.None);
        var output = stdout.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("CREATE 2 field(s)", output);
        Assert.Contains("Activity", output);
        Assert.Contains("priority", output);
        Assert.Contains("CREATE 1 label(s)", output);
        Assert.Contains("type:task", output);
        Assert.Contains("Re-run with --apply", output);

        // Crucial: applier was NOT called in dry-run.
        Assert.Empty(applier.Fields);
        Assert.Empty(applier.Labels);
    }

    [Fact]
    public async Task ApplyMode_CreatesFieldsAndLabels()
    {
        var workflow = MinimalExample();
        var shape = new BoardShape(["Ready"], [], []);
        var applier = new RecordingApplier();
        var stdout = new StringWriter();
        var runner = new ScaffoldBoardRunner(
            workflow, new CannedProbe(shape), applier, NullLogger.Instance, stdout);

        var exit = await runner.RunAsync("123", apply: true, CancellationToken.None);
        var output = stdout.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("Apply complete", output);

        // Both fields created
        Assert.Equal(2, applier.Fields.Count);
        Assert.Contains(applier.Fields, f => f.Field == "Activity" && f.Options.Contains("Design"));
        Assert.Contains(applier.Fields, f => f.Field == "priority" && f.Options.Contains("Urgent"));

        // Label created
        Assert.Single(applier.Labels);
        Assert.Equal("type:task", applier.Labels[0]);
    }

    [Fact]
    public async Task ApplyMode_AlreadyExistsOutcome_TreatedAsSuccess()
    {
        var workflow = MinimalExample();
        var shape = new BoardShape(["Ready"], [], []);
        // Applier reports everything as already-exists (idempotent re-run).
        var applier = new RecordingApplier
        {
            FieldOutcome = ApplyOutcome.AlreadyExists,
            LabelOutcome = ApplyOutcome.AlreadyExists,
        };
        var stdout = new StringWriter();
        var runner = new ScaffoldBoardRunner(
            workflow, new CannedProbe(shape), applier, NullLogger.Instance, stdout);

        var exit = await runner.RunAsync("123", apply: true, CancellationToken.None);

        Assert.Equal(0, exit);   // already-exists is NOT a failure
        Assert.Contains("Apply complete", stdout.ToString());
    }

    [Fact]
    public async Task ApplyMode_AnyFailure_ReturnsExitOne()
    {
        var workflow = MinimalExample();
        var shape = new BoardShape(["Ready"], [], []);
        var applier = new RecordingApplier { FieldOutcome = ApplyOutcome.Failed };
        var stdout = new StringWriter();
        var runner = new ScaffoldBoardRunner(
            workflow, new CannedProbe(shape), applier, NullLogger.Instance, stdout);

        var exit = await runner.RunAsync("123", apply: true, CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.Contains("failure(s)", stdout.ToString());
    }

    [Fact]
    public async Task DryRun_ManualOnlyPlan_HintsManualActionRequired()
    {
        // Existing field with missing options → field-option gap (manual).
        var workflow = MinimalExample();
        var shape = new BoardShape(
            ColumnNames: ["Ready"],
            Fields:
            [
                new BoardField("Activity", "SINGLE_SELECT", [new BoardFieldOption("Design", "x")]),
                new BoardField("priority", "SINGLE_SELECT",
                [
                    new BoardFieldOption("Urgent", "x"),
                    // Desired missing on existing priority field
                ]),
            ],
            Labels: ["type:task"]);
        var applier = new RecordingApplier();
        var stdout = new StringWriter();
        var runner = new ScaffoldBoardRunner(
            workflow, new CannedProbe(shape), applier, NullLogger.Instance, stdout);

        var exit = await runner.RunAsync("123", apply: false, CancellationToken.None);
        var output = stdout.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("WARN", output);
        Assert.Contains("priority", output);
        Assert.Contains("Desired", output);
        Assert.Contains("Manual actions", output);
        // No CREATE actions exist — only the manual warning.
        Assert.DoesNotContain("CREATE", output);
    }

    [Fact]
    public async Task ApplyMode_OnlyManualActions_NoApplyButReturnsZero()
    {
        var workflow = MinimalExample();
        // Existing fields/labels — only thing missing is options on existing field.
        var shape = new BoardShape(
            ColumnNames: ["Ready"],
            Fields:
            [
                new BoardField("Activity", "SINGLE_SELECT", [new BoardFieldOption("Design", "x")]),
                new BoardField("priority", "SINGLE_SELECT",
                [
                    new BoardFieldOption("Urgent", "x"),
                ]),
            ],
            Labels: ["type:task"]);
        var applier = new RecordingApplier();
        var stdout = new StringWriter();
        var runner = new ScaffoldBoardRunner(
            workflow, new CannedProbe(shape), applier, NullLogger.Instance, stdout);

        var exit = await runner.RunAsync("123", apply: true, CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("Nothing to apply", stdout.ToString());
        Assert.Empty(applier.Fields);
        Assert.Empty(applier.Labels);
    }

    [Fact]
    public async Task ProbeReturnsNull_ProviderUnsupportedReturnsExitOne()
    {
        var runner = new ScaffoldBoardRunner(
            MinimalExample(), new CannedProbe(null), new RecordingApplier(),
            NullLogger.Instance, new StringWriter());

        var exit = await runner.RunAsync("123", apply: false, CancellationToken.None);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task ProbeThrows_ReturnsExitOne()
    {
        var runner = new ScaffoldBoardRunner(
            MinimalExample(),
            new CannedProbe(null, new InvalidOperationException("gh not authenticated")),
            new RecordingApplier(),
            NullLogger.Instance,
            new StringWriter());

        var exit = await runner.RunAsync("123", apply: false, CancellationToken.None);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task ApplyMode_NoApplier_ReturnsExitOne()
    {
        var workflow = MinimalExample();
        var shape = new BoardShape(["Ready"], [], []);
        var nullApplier = new NullBoardShapeApplier();
        var stdout = new StringWriter();
        var runner = new ScaffoldBoardRunner(
            workflow, new CannedProbe(shape), nullApplier, NullLogger.Instance, stdout);

        var exit = await runner.RunAsync("123", apply: true, CancellationToken.None);

        Assert.Equal(1, exit);
    }
}
