using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using TaskBoard.Worker.Tests.Helpers;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Pins each output path of <see cref="DiagnoseRunner"/>: the goal is to make
/// sure every path produces a useful first-line answer for "why isn't this
/// card being picked up?" The headline test is the assigned-card filter
/// mismatch — that's the operator pitfall this whole mode exists to catch.
/// </summary>
public class DiagnoseRunnerTests
{
    /// <summary>
    /// Builds a KvA-style workflow with single Ready column + Activity field +
    /// assignee-isEmpty filter. Mirrors the from-scratch templates so tests
    /// pin the same shape new projects will run against.
    /// </summary>
    private static WorkflowConfig BuildKvaShapeWorkflow() => new(
        States: new()
        {
            ["backlog"] = new WorkflowState(
                Name: "Backlog",
                Role: null,
                GateType: GateTypes.ManualEntry,
                TaskPrompt: null,
                Transitions: new(),
                Column: "Backlog"),
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
            ["implementation"] = new WorkflowState(
                Name: "Implementation",
                Role: null,
                GateType: GateTypes.AgentRun,
                TaskPrompt: null,
                Transitions: new()
                {
                    [TransitionKeys.Complete] = new TransitionTarget([
                        new TransitionAction(ActionTypes.Unassign),
                        new TransitionAction(ActionTypes.SetField, Field: "Activity", Value: "Review"),
                        new TransitionAction(ActionTypes.MoveToColumn, Value: "Ready"),
                    ]),
                },
                Column: "Ready",
                Filters:
                [
                    new CardFilter(FilterTypes.Field, FilterOperators.Equals, Field: "Activity", Value: "Implementation"),
                    new CardFilter(FilterTypes.Assignee, FilterOperators.IsEmpty),
                ]),
            ["questions"] = new WorkflowState(
                Name: "Questions",
                Role: null,
                GateType: GateTypes.Holding,
                TaskPrompt: null,
                Transitions: new(),
                Column: "Questions"),
            ["done"] = new WorkflowState(
                Name: "Done",
                Role: null,
                GateType: GateTypes.Terminal,
                TaskPrompt: null,
                Transitions: new(),
                Column: "Done"),
            ["in_progress"] = new WorkflowState(
                Name: "In progress",
                Role: null,
                GateType: GateTypes.InProgress,
                TaskPrompt: null,
                Transitions: new(),
                Column: "In progress"),
        },
        Roles: new());

    /// <summary>
    /// Stub board client that returns a single canned card.
    /// </summary>
    private sealed class CannedBoard(BoardCard card) : ITaskBoardClient
    {
        public Task<BoardCard> GetCardAsync(string cardId, CancellationToken ct)
            => Task.FromResult(card);
        public Task<IReadOnlyList<BoardCard>> GetBoardCardsAsync(
            string boardId, CancellationToken ct, IReadOnlyList<string>? excludeStatuses = null)
            => Task.FromResult<IReadOnlyList<BoardCard>>([card]);
        public Task UpdateCardBodyAsync(string cardId, string body, CancellationToken ct) => Task.CompletedTask;
        public Task MoveCardToColumnAsync(string cardId, string columnId, CancellationToken ct) => Task.CompletedTask;
        public Task UpsertAgentCommentAsync(string cardId, string body, string marker, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<CardComment>> GetCardCommentsAsync(string cardId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CardComment>>([]);
        public Task<string> CreateCardAsync(CreateCardRequest request, CancellationToken ct) => Task.FromResult("0");
        public Task AddLabelAsync(string cardId, string labelName, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveLabelAsync(string cardId, string labelName, CancellationToken ct) => Task.CompletedTask;
        public Task AssignAsync(string cardId, string username, CancellationToken ct) => Task.CompletedTask;
        public Task UnassignAsync(string cardId, string? username, CancellationToken ct) => Task.CompletedTask;
        public Task SetFieldAsync(string cardId, string fieldName, string value, CancellationToken ct) => Task.CompletedTask;
        public Task ClearFieldAsync(string cardId, string fieldName, CancellationToken ct) => Task.CompletedTask;
        public Task<string> GetCurrentUserAsync(CancellationToken ct) => Task.FromResult("agent-bot");
    }

    private static async Task<string> RunDiagnose(BoardCard card, WorkflowConfig workflow)
    {
        var board = new CannedBoard(card);
        var stdout = new StringWriter();
        var runner = new DiagnoseRunner(board, workflow, NullLogger.Instance, stdout);
        var exit = await runner.RunAsync(card.Id, CancellationToken.None);
        Assert.Equal(0, exit);
        return stdout.ToString();
    }

    [Fact]
    public async Task EligibleCard_ReportsELIGIBLE()
    {
        var card = new BoardCard(
            Id: "1", Title: "Eligible card", Body: "...", ColumnId: "Ready",
            Metadata: new Dictionary<string, string> { ["Activity"] = "Design" },
            Assignees: []);

        var output = await RunDiagnose(card, BuildKvaShapeWorkflow());

        Assert.Contains("Resolved state: Design", output);
        Assert.Contains("ELIGIBLE", output);
        Assert.Contains("aiboard --mode polling", output);
    }

    [Fact]
    public async Task AssignedCard_ReportsAssigneeFilterMismatchWithUnassignHint()
    {
        // Headline regression target: card sat in Ready/Activity=Design but has
        // someone assigned. Operator forgot that assignment is the WIP lock.
        var card = new BoardCard(
            Id: "42", Title: "Stuck because assigned", Body: "...", ColumnId: "Ready",
            Metadata: new Dictionary<string, string> { ["Activity"] = "Design" },
            Assignees: ["nicholas"]);

        var output = await RunDiagnose(card, BuildKvaShapeWorkflow());

        Assert.Contains("SKIPPED", output);
        Assert.Contains("assigned to @nicholas", output);
        Assert.Contains("Most likely cause: card is assigned", output);
        Assert.Contains("gh issue edit 42", output);
        Assert.Contains("--remove-assignee nicholas", output);
        // Should NOT trip the field-mismatch headline since we know the
        // assignee is the actual blocker.
        Assert.DoesNotContain("Most likely cause: a field value", output);
    }

    [Fact]
    public async Task FieldFilterMismatch_ReportsFieldHint()
    {
        // Activity is unset (operator dragged a card into Ready but didn't
        // pick a stage). All states require Activity=<something>.
        var card = new BoardCard(
            Id: "5", Title: "Missing Activity", Body: "...", ColumnId: "Ready",
            Metadata: new Dictionary<string, string>(),
            Assignees: []);

        var output = await RunDiagnose(card, BuildKvaShapeWorkflow());

        Assert.Contains("SKIPPED", output);
        Assert.Contains("field Activity = 'Design' ✗ is (unset)", output);
        Assert.Contains("Most likely cause: a field value", output);
    }

    [Fact]
    public async Task FieldWrongValue_ReportsActualVsExpected()
    {
        var card = new BoardCard(
            Id: "6", Title: "Wrong stage", Body: "...", ColumnId: "Ready",
            Metadata: new Dictionary<string, string> { ["Activity"] = "Implementation" },
            Assignees: []);

        var output = await RunDiagnose(card, BuildKvaShapeWorkflow());

        // 'implementation' state matches Activity=Implementation; this card should
        // actually resolve cleanly. Confirm so.
        Assert.Contains("Resolved state: Implementation", output);
        Assert.Contains("ELIGIBLE", output);
    }

    [Fact]
    public async Task HoldingColumn_ReportsHoldingExplanation()
    {
        var card = new BoardCard(
            Id: "9", Title: "Has questions", Body: "...", ColumnId: "Questions",
            Metadata: new Dictionary<string, string>(),
            Assignees: []);

        var output = await RunDiagnose(card, BuildKvaShapeWorkflow());

        Assert.Contains("HOLDING", output);
        Assert.Contains("Questions", output);
    }

    [Fact]
    public async Task TerminalColumn_ReportsDONE()
    {
        var card = new BoardCard(
            Id: "10", Title: "Finished", Body: "...", ColumnId: "Done",
            Metadata: new Dictionary<string, string>(),
            Assignees: []);

        var output = await RunDiagnose(card, BuildKvaShapeWorkflow());

        Assert.Contains("DONE", output);
        Assert.Contains("Resolved state: Done", output);
    }

    [Fact]
    public async Task ManualEntryColumn_ReportsEntryHint()
    {
        var card = new BoardCard(
            Id: "11", Title: "Sitting in backlog", Body: "...", ColumnId: "Backlog",
            Metadata: new Dictionary<string, string>(),
            Assignees: []);

        var output = await RunDiagnose(card, BuildKvaShapeWorkflow());

        Assert.Contains("ENTRY", output);
        // Should advise moving to a Ready column.
        Assert.Contains("Move the card to", output);
    }

    [Fact]
    public async Task UnknownColumn_ReportsNotInWorkflow()
    {
        var card = new BoardCard(
            Id: "12", Title: "Where is this?", Body: "...", ColumnId: "Lost in Space",
            Metadata: new Dictionary<string, string>(),
            Assignees: []);

        var output = await RunDiagnose(card, BuildKvaShapeWorkflow());

        Assert.Contains("NOT IN WORKFLOW", output);
        Assert.Contains("'Lost in Space'", output);
        // Should suggest known actionable columns.
        Assert.Contains("Ready", output);
    }

    [Fact]
    public async Task InProgressColumn_ReportsInProgressNote()
    {
        var card = new BoardCard(
            Id: "13", Title: "Working", Body: "...", ColumnId: "In progress",
            Metadata: new Dictionary<string, string>(),
            Assignees: ["agent-bot"]);

        var output = await RunDiagnose(card, BuildKvaShapeWorkflow());

        Assert.Contains("IN PROGRESS", output);
        Assert.Contains("Resolved state: In progress", output);
    }

    [Fact]
    public async Task LabelFilterMismatch_ReportsMissingLabel()
    {
        // Custom workflow with a label filter on the design state to verify
        // the label branch.
        var workflow = new WorkflowConfig(
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
                        new CardFilter(FilterTypes.Label, FilterOperators.Exists, Value: "type:task"),
                    ]),
            },
            Roles: new());

        var card = new BoardCard(
            Id: "20", Title: "No label", Body: "...", ColumnId: "Ready",
            Labels: ["bug"],
            Assignees: []);

        var output = await RunDiagnose(card, workflow);

        Assert.Contains("SKIPPED", output);
        Assert.Contains("label 'type:task' required", output);
        Assert.Contains("Most likely cause: a label is missing", output);
    }

    [Fact]
    public async Task AmbiguousResolution_ReportsAmbiguousAndAdvisesFilters()
    {
        // Two states share the same column with no filters → ResolveState throws.
        var workflow = new WorkflowConfig(
            States: new()
            {
                ["design"] = new WorkflowState(
                    Name: "Design",
                    Role: null,
                    GateType: GateTypes.AgentRun,
                    TaskPrompt: null,
                    Transitions: new(),
                    Column: "Ready"),
                ["impl"] = new WorkflowState(
                    Name: "Implementation",
                    Role: null,
                    GateType: GateTypes.AgentRun,
                    TaskPrompt: null,
                    Transitions: new(),
                    Column: "Ready"),
            },
            Roles: new());

        var card = new BoardCard(
            Id: "30", Title: "Ambig", Body: "...", ColumnId: "Ready",
            Assignees: []);

        var output = await RunDiagnose(card, workflow);

        Assert.Contains("AMBIGUOUS", output);
        Assert.Contains("filter", output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Header_AlwaysIncludesAssigneesAndRelevantFields()
    {
        var card = new BoardCard(
            Id: "1", Title: "T", Body: "B", ColumnId: "Ready",
            Metadata: new Dictionary<string, string>
            {
                ["Activity"] = "Design",
                ["priority"] = "Critical",
                ["EmptyField"] = "",
            },
            Labels: ["type:task"],
            Assignees: ["alice", "bob"]);

        var output = await RunDiagnose(card, BuildKvaShapeWorkflow());

        Assert.Contains("Card 1: T", output);
        Assert.Contains("Column:    Ready", output);
        Assert.Contains("Assignees: @alice, @bob", output);
        Assert.Contains("Labels:    type:task", output);
        Assert.Contains("Activity=Design", output);
        Assert.Contains("priority=Critical", output);
        Assert.DoesNotContain("EmptyField=", output);   // empty values omitted
    }

    [Fact]
    public async Task AssignedCardWithWrongActivityToo_StillRoutesToAssigneeHeadline()
    {
        // Regression guard for the structured-routing refactor: when both the
        // assignee filter AND a field filter fail, the assignee fix takes
        // priority because that's the operator pitfall that bites most often.
        // Pre-refactor, this depended on substring-matching the description text.
        var card = new BoardCard(
            Id: "77", Title: "Both wrong", Body: "...", ColumnId: "Ready",
            Metadata: new Dictionary<string, string> { ["Activity"] = "Implementation" },
            Assignees: ["nicholas"]);

        var output = await RunDiagnose(card, BuildKvaShapeWorkflow());

        // Headline must be the assignee fix, NOT field-mismatch, even though
        // the implementation state's assignee filter also fails.
        Assert.Contains("Most likely cause: card is assigned", output);
        Assert.Contains("--remove-assignee nicholas", output);
        Assert.DoesNotContain("Most likely cause: a field value", output);
        Assert.DoesNotContain("Most likely cause: a label", output);
    }

    /// <summary>
    /// Stub dependency client returning a fixed blocker list. Sufficient for
    /// driving DependencyGuard in diagnose's read-only mode (no AddBlockedBy /
    /// RemoveBlockedBy calls reach it).
    /// </summary>
    private sealed class CannedDependencyClient(IReadOnlyList<CardDependency> blockers) : ICardDependencyClient
    {
        public Task<IReadOnlyList<CardDependency>> GetBlockersAsync(string cardId, CancellationToken ct)
            => Task.FromResult(blockers);
        public Task<IReadOnlyList<CardDependency>> GetBlockedCardsAsync(string cardId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CardDependency>>([]);
        public Task AddBlockedByAsync(string blockedCardId, string blockerCardId, CancellationToken ct)
            => throw new InvalidOperationException("AddBlockedBy must not be called from diagnose");
        public Task RemoveBlockedByAsync(string blockedCardId, string blockerCardId, CancellationToken ct)
            => throw new InvalidOperationException("RemoveBlockedBy must not be called from diagnose");
    }

    [Fact]
    public async Task DependencyBlockedCard_ReportsBlockedAndListsUnresolvedBlockers()
    {
        // Card is otherwise eligible (Activity=Design, unassigned), but a
        // dependency policy is enabled and the blocker (#5) is not in a
        // satisfied column. Pre-fix, diagnose would say ELIGIBLE — confusing
        // because polling silently skips the card.
        var workflow = BuildKvaShapeWorkflow() with
        {
            DependencyPolicy = new DependencyPolicy(
                Enabled: true,
                EnforcedStates: ["Design", "Implementation"],
                SatisfiedColumns: ["Done"]),
        };
        var card = new BoardCard(
            Id: "10", Title: "Blocked but otherwise eligible", Body: "...", ColumnId: "Ready",
            Metadata: new Dictionary<string, string> { ["Activity"] = "Design" },
            Assignees: []);

        var board = new CannedBoard(card);
        var deps = new CannedDependencyClient(
            [new CardDependency("5", Title: "Create database", ColumnId: "Ready", IsClosed: false)]);
        var guard = new DependencyGuard(
            board, deps, TestWorkflowConfigProvider.Create(workflow),
            NullDependencyWaitStore.Instance,
            NullLogger<DependencyGuard>.Instance);

        var stdout = new StringWriter();
        var runner = new DiagnoseRunner(board, workflow, NullLogger.Instance, stdout, guard);
        var exit = await runner.RunAsync(card.Id, CancellationToken.None);
        var output = stdout.ToString();

        Assert.Equal(0, exit);
        Assert.Contains("BLOCKED BY DEPENDENCIES", output);
        Assert.Contains("#5", output);
        Assert.Contains("Create database", output);
        // Must NOT report ELIGIBLE — that was the pre-fix confusion path.
        Assert.DoesNotContain("Pickup result: ELIGIBLE", output);
    }

    [Fact]
    public async Task DependencyBlockedCard_NoSideEffectsToBoard()
    {
        // Diagnose is read-only. DependencyGuard.CheckAsync's blocked-comment
        // upsert and waitStore write must NOT fire from the diagnose path.
        var workflow = BuildKvaShapeWorkflow() with
        {
            DependencyPolicy = new DependencyPolicy(
                Enabled: true,
                EnforcedStates: ["Design"],
                SatisfiedColumns: ["Done"],
                CommentOnBlocked: true),
        };
        var card = new BoardCard(
            Id: "10", Title: "Blocked", Body: "...", ColumnId: "Ready",
            Metadata: new Dictionary<string, string> { ["Activity"] = "Design" },
            Assignees: []);

        var commentCalls = 0;
        var board = new CommentRecordingBoard(card, () => commentCalls++);
        var deps = new CannedDependencyClient(
            [new CardDependency("5", Title: "Blocker", ColumnId: "Ready", IsClosed: false)]);
        var waitStore = new RecordingWaitStore();
        var guard = new DependencyGuard(
            board, deps, TestWorkflowConfigProvider.Create(workflow), waitStore, NullLogger<DependencyGuard>.Instance);

        var runner = new DiagnoseRunner(board, workflow, NullLogger.Instance, new StringWriter(), guard);
        await runner.RunAsync(card.Id, CancellationToken.None);

        Assert.Equal(0, commentCalls);
        Assert.Equal(0, waitStore.Calls);
    }

    private sealed class CommentRecordingBoard(BoardCard card, Action onComment) : ITaskBoardClient
    {
        public Task<BoardCard> GetCardAsync(string cardId, CancellationToken ct) => Task.FromResult(card);
        public Task<IReadOnlyList<BoardCard>> GetBoardCardsAsync(string boardId, CancellationToken ct, IReadOnlyList<string>? excludeStatuses = null)
            => Task.FromResult<IReadOnlyList<BoardCard>>([card]);
        public Task UpdateCardBodyAsync(string cardId, string body, CancellationToken ct) => Task.CompletedTask;
        public Task MoveCardToColumnAsync(string cardId, string columnId, CancellationToken ct) => Task.CompletedTask;
        public Task UpsertAgentCommentAsync(string cardId, string body, string marker, CancellationToken ct)
        {
            onComment();
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<CardComment>> GetCardCommentsAsync(string cardId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CardComment>>([]);
        public Task<string> CreateCardAsync(CreateCardRequest request, CancellationToken ct) => Task.FromResult("0");
        public Task AddLabelAsync(string cardId, string labelName, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveLabelAsync(string cardId, string labelName, CancellationToken ct) => Task.CompletedTask;
        public Task AssignAsync(string cardId, string username, CancellationToken ct) => Task.CompletedTask;
        public Task UnassignAsync(string cardId, string? username, CancellationToken ct) => Task.CompletedTask;
        public Task SetFieldAsync(string cardId, string fieldName, string value, CancellationToken ct) => Task.CompletedTask;
        public Task ClearFieldAsync(string cardId, string fieldName, CancellationToken ct) => Task.CompletedTask;
        public Task<string> GetCurrentUserAsync(CancellationToken ct) => Task.FromResult("agent-bot");
    }

    private sealed class RecordingWaitStore : IDependencyWaitStore
    {
        public int Calls { get; private set; }
        public Task RecordBlockedAsync(string cardId, IReadOnlyList<CardDependency> unresolvedBlockers, string source, CancellationToken ct)
        {
            Calls++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task CardNotFound_ReturnsNonZeroExitFromBoardError()
    {
        // Board client returns null — runner should surface as exit 1.
        // (We can't easily cover this with the canned stub; use a tiny
        // throw-style stub that mimics 'card vanished'.)
        var board = new ThrowingBoard();
        var workflow = BuildKvaShapeWorkflow();
        var stdout = new StringWriter();
        var runner = new DiagnoseRunner(board, workflow, NullLogger.Instance, stdout);

        var exit = await runner.RunAsync("999", CancellationToken.None);

        Assert.Equal(1, exit);
    }

    private sealed class ThrowingBoard : ITaskBoardClient
    {
        public Task<BoardCard> GetCardAsync(string cardId, CancellationToken ct)
            => throw new InvalidOperationException("card not found");
        public Task<IReadOnlyList<BoardCard>> GetBoardCardsAsync(string boardId, CancellationToken ct, IReadOnlyList<string>? excludeStatuses = null)
            => Task.FromResult<IReadOnlyList<BoardCard>>([]);
        public Task UpdateCardBodyAsync(string cardId, string body, CancellationToken ct) => Task.CompletedTask;
        public Task MoveCardToColumnAsync(string cardId, string columnId, CancellationToken ct) => Task.CompletedTask;
        public Task UpsertAgentCommentAsync(string cardId, string body, string marker, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<CardComment>> GetCardCommentsAsync(string cardId, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<CardComment>>([]);
        public Task<string> CreateCardAsync(CreateCardRequest request, CancellationToken ct) => Task.FromResult("0");
        public Task AddLabelAsync(string cardId, string labelName, CancellationToken ct) => Task.CompletedTask;
        public Task RemoveLabelAsync(string cardId, string labelName, CancellationToken ct) => Task.CompletedTask;
        public Task AssignAsync(string cardId, string username, CancellationToken ct) => Task.CompletedTask;
        public Task UnassignAsync(string cardId, string? username, CancellationToken ct) => Task.CompletedTask;
        public Task SetFieldAsync(string cardId, string fieldName, string value, CancellationToken ct) => Task.CompletedTask;
        public Task ClearFieldAsync(string cardId, string fieldName, CancellationToken ct) => Task.CompletedTask;
        public Task<string> GetCurrentUserAsync(CancellationToken ct) => Task.FromResult("agent-bot");
    }
}
