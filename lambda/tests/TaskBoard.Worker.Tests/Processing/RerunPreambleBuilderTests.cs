using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Unit tests for <see cref="RerunPreambleBuilder"/>: pinning the
/// "marker present + prior COMPLETE" detection rule and the
/// preamble shape across both variants.
/// </summary>
public class RerunPreambleBuilderTests
{
    private const string CardId = "42";
    private const string StateName = "Ready for Design";
    private const string StepName = "review_related_tickets";
    private const string Marker = "agent-step:review_related_tickets";
    private const string CurrentRun = "run-current";
    private const string PriorRun = "run-prior";

    [Fact]
    public async Task MarkerAbsent_ReturnsNull()
    {
        var store = new StubRunStore(records: []);
        var builder = NewBuilder(store);

        var preamble = await builder.TryBuildPreambleAsync(
            CardId, StateName, StepName, Marker, CurrentRun,
            existingComments: [],
            variant: PreambleVariant.TaskPrompt,
            cancellationToken: CancellationToken.None);

        Assert.Null(preamble);
    }

    [Fact]
    public async Task MarkerPresent_NoMatchingDbRecord_ReturnsNull()
    {
        var store = new StubRunStore(records: []);
        var builder = NewBuilder(store);
        var comments = OneCommentWithMarker(Marker, body: "prior content");

        var preamble = await builder.TryBuildPreambleAsync(
            CardId, StateName, StepName, Marker, CurrentRun,
            existingComments: comments,
            variant: PreambleVariant.TaskPrompt,
            cancellationToken: CancellationToken.None);

        Assert.Null(preamble);
    }

    [Fact]
    public async Task MarkerPresent_PriorOutcomeComplete_ReturnsPreamble()
    {
        var store = new StubRunStore(
            records: [PriorStep(StepName, AgentOutcome.COMPLETE)]);
        var builder = NewBuilder(store);
        var comments = OneCommentWithMarker(Marker,
            body: "Reviewed tickets #5 and #12; no conflicts.");

        var preamble = await builder.TryBuildPreambleAsync(
            CardId, StateName, StepName, Marker, CurrentRun,
            existingComments: comments,
            variant: PreambleVariant.TaskPrompt,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(preamble);
        Assert.Contains("RE-RUN OF PREVIOUSLY COMPLETED STEP", preamble);
        Assert.Contains("Reviewed tickets #5 and #12", preamble);
        Assert.Contains("Confirmed prior output remains accurate.", preamble);
    }

    [Fact]
    public async Task MarkerPresent_PriorOutcomeNeedsInfo_ReturnsNull()
    {
        var store = new StubRunStore(
            records: [PriorStep(StepName, AgentOutcome.NEEDS_INFO)]);
        var builder = NewBuilder(store);
        var comments = OneCommentWithMarker(Marker, body: "prior content");

        var preamble = await builder.TryBuildPreambleAsync(
            CardId, StateName, StepName, Marker, CurrentRun,
            existingComments: comments,
            variant: PreambleVariant.TaskPrompt,
            cancellationToken: CancellationToken.None);

        Assert.Null(preamble);
    }

    [Fact]
    public async Task MarkerPresent_PriorOutcomeError_ReturnsNull()
    {
        var store = new StubRunStore(
            records: [PriorStep(StepName, AgentOutcome.ERROR)]);
        var builder = NewBuilder(store);
        var comments = OneCommentWithMarker(Marker, body: "prior content");

        var preamble = await builder.TryBuildPreambleAsync(
            CardId, StateName, StepName, Marker, CurrentRun,
            existingComments: comments,
            variant: PreambleVariant.TaskPrompt,
            cancellationToken: CancellationToken.None);

        Assert.Null(preamble);
    }

    [Fact]
    public async Task MarkerPresent_PriorBodyEmptyAfterStrip_ReturnsNull()
    {
        var store = new StubRunStore(
            records: [PriorStep(StepName, AgentOutcome.COMPLETE)]);
        var builder = NewBuilder(store);
        // Body is just the marker line and whitespace — nothing useful to feed back.
        var comments = new[]
        {
            new CardComment("agent", $"<!-- {Marker} -->\n   \n", DateTimeOffset.UtcNow)
        };

        var preamble = await builder.TryBuildPreambleAsync(
            CardId, StateName, StepName, Marker, CurrentRun,
            existingComments: comments,
            variant: PreambleVariant.TaskPrompt,
            cancellationToken: CancellationToken.None);

        Assert.Null(preamble);
    }

    [Fact]
    public async Task CurrentRunRow_Excluded_ReturnsNullWhenNoOtherRuns()
    {
        // The current run already wrote a step_result row (e.g. a prior step
        // in the same run). The preamble lookup must NOT treat the current
        // run's own row as "prior" — it should look further back.
        var store = new StubRunStore(records: [
            new StubRecord(StepName, AgentOutcome.COMPLETE, RunId: CurrentRun,
                CompletedAt: DateTimeOffset.UtcNow)
        ]);
        var builder = NewBuilder(store);
        var comments = OneCommentWithMarker(Marker, body: "stale");

        var preamble = await builder.TryBuildPreambleAsync(
            CardId, StateName, StepName, Marker, CurrentRun,
            existingComments: comments,
            variant: PreambleVariant.TaskPrompt,
            cancellationToken: CancellationToken.None);

        Assert.Null(preamble);
    }

    [Fact]
    public async Task PriorRunComplete_PrecedesNewerCurrentRunRow_StillFinds()
    {
        // Records in completed_at_utc DESC: current run's row first, prior run's row second.
        // The matcher must skip the current run's row and find the prior one.
        var now = DateTimeOffset.UtcNow;
        var store = new StubRunStore(records: [
            new StubRecord(StepName, AgentOutcome.NEEDS_INFO, RunId: CurrentRun,
                CompletedAt: now),
            new StubRecord(StepName, AgentOutcome.COMPLETE, RunId: PriorRun,
                CompletedAt: now.AddMinutes(-5)),
        ]);
        var builder = NewBuilder(store);
        var comments = OneCommentWithMarker(Marker, body: "previously COMPLETE");

        var preamble = await builder.TryBuildPreambleAsync(
            CardId, StateName, StepName, Marker, CurrentRun,
            existingComments: comments,
            variant: PreambleVariant.TaskPrompt,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(preamble);
        Assert.Contains("previously COMPLETE", preamble);
    }

    [Fact]
    public async Task MultiSlot_PriorEvaluatorRowSlotN_FindsViaSuffixMatch()
    {
        // Multi-slot step's evaluator row carries `:slot-1:evaluator` suffix.
        // Helper must still match the canonical step name "create_design".
        var canonicalStepName = "create_design";
        var canonicalMarker = $"agent-step:{canonicalStepName}";
        var store = new StubRunStore(records: [
            new StubRecord(
                StepName: $"{canonicalStepName}:slot-1:evaluator",
                Outcome: AgentOutcome.COMPLETE,
                RunId: PriorRun,
                CompletedAt: DateTimeOffset.UtcNow.AddMinutes(-5))
        ]);
        var builder = NewBuilder(store);
        var comments = OneCommentWithMarker(canonicalMarker, body: "prior winning design");

        var preamble = await builder.TryBuildPreambleAsync(
            CardId, StateName, canonicalStepName, canonicalMarker, CurrentRun,
            existingComments: comments,
            variant: PreambleVariant.TaskPrompt,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(preamble);
        Assert.Contains("prior winning design", preamble);
    }

    [Fact]
    public async Task SingleSlotEvaluator_NoSlotInfix_FindsViaSuffixMatch()
    {
        // Single-slot evaluator row is just `:evaluator` (no `:slot-N`).
        var canonicalStepName = "create_design";
        var canonicalMarker = $"agent-step:{canonicalStepName}";
        var store = new StubRunStore(records: [
            new StubRecord(
                StepName: $"{canonicalStepName}:evaluator",
                Outcome: AgentOutcome.COMPLETE,
                RunId: PriorRun,
                CompletedAt: DateTimeOffset.UtcNow.AddMinutes(-5))
        ]);
        var builder = NewBuilder(store);
        var comments = OneCommentWithMarker(canonicalMarker, body: "prior winning design");

        var preamble = await builder.TryBuildPreambleAsync(
            CardId, StateName, canonicalStepName, canonicalMarker, CurrentRun,
            existingComments: comments,
            variant: PreambleVariant.TaskPrompt,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(preamble);
    }

    [Fact]
    public async Task EvaluatorVariant_ProducesEvaluatorPreamble()
    {
        var store = new StubRunStore(records: [PriorStep(StepName, AgentOutcome.COMPLETE)]);
        var builder = NewBuilder(store);
        var comments = OneCommentWithMarker(Marker, body: "prior winner content");

        var preamble = await builder.TryBuildPreambleAsync(
            CardId, StateName, StepName, Marker, CurrentRun,
            existingComments: comments,
            variant: PreambleVariant.Evaluator,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(preamble);
        Assert.Contains("All candidates confirmed prior output remains accurate.", preamble);
        Assert.Contains("winner_index: 0", preamble);
        // And the evaluator variant should NOT contain the candidate-side instruction
        Assert.DoesNotContain("Confirmed prior output remains accurate.\"`. Do not redo any analysis", preamble);
    }

    [Fact]
    public async Task TaskPromptVariant_ProducesCandidatePreamble()
    {
        var store = new StubRunStore(records: [PriorStep(StepName, AgentOutcome.COMPLETE)]);
        var builder = NewBuilder(store);
        var comments = OneCommentWithMarker(Marker, body: "prior content");

        var preamble = await builder.TryBuildPreambleAsync(
            CardId, StateName, StepName, Marker, CurrentRun,
            existingComments: comments,
            variant: PreambleVariant.TaskPrompt,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(preamble);
        Assert.Contains("Confirmed prior output remains accurate.", preamble);
        Assert.DoesNotContain("All candidates confirmed", preamble);
    }

    [Fact]
    public async Task TwoCommentsWithSameMarker_PicksLatestByCreatedAt()
    {
        // Defensive: if the upsert path ever produced duplicates, take the most recent.
        var store = new StubRunStore(records: [PriorStep(StepName, AgentOutcome.COMPLETE)]);
        var builder = NewBuilder(store);
        var older = new CardComment(
            "agent", $"<!-- {Marker} -->\nOLDER content", DateTimeOffset.UtcNow.AddMinutes(-30));
        var newer = new CardComment(
            "agent", $"<!-- {Marker} -->\nNEWER content", DateTimeOffset.UtcNow);

        var preamble = await builder.TryBuildPreambleAsync(
            CardId, StateName, StepName, Marker, CurrentRun,
            existingComments: [older, newer],
            variant: PreambleVariant.TaskPrompt,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(preamble);
        Assert.Contains("NEWER content", preamble);
        Assert.DoesNotContain("OLDER content", preamble);
    }

    [Fact]
    public async Task MarkerNotAtStartOfBody_StillFound_AndStripped()
    {
        // Some upsert paths emit the marker as part of a longer body (prefix line, then marker, then body).
        // The matcher uses Contains, and StripMarker removes the first occurrence — so the prior body
        // is what's left after stripping. Verify both halves are preserved.
        var store = new StubRunStore(records: [PriorStep(StepName, AgentOutcome.COMPLETE)]);
        var builder = NewBuilder(store);
        var body = $"## Step Header\n<!-- {Marker} -->\nthe useful content";
        var comments = new[] { new CardComment("agent", body, DateTimeOffset.UtcNow) };

        var preamble = await builder.TryBuildPreambleAsync(
            CardId, StateName, StepName, Marker, CurrentRun,
            existingComments: comments,
            variant: PreambleVariant.TaskPrompt,
            cancellationToken: CancellationToken.None);

        Assert.NotNull(preamble);
        Assert.Contains("the useful content", preamble);
        Assert.Contains("Step Header", preamble);
        // The marker itself should NOT be embedded in the preamble.
        Assert.DoesNotContain($"<!-- {Marker} -->", preamble);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static RerunPreambleBuilder NewBuilder(StubRunStore store)
        => new(store, NullLogger<RerunPreambleBuilder>.Instance);

    private static CardComment[] OneCommentWithMarker(string marker, string body)
        => [new CardComment("agent", $"<!-- {marker} -->\n{body}", DateTimeOffset.UtcNow)];

    private static StubRecord PriorStep(string stepName, AgentOutcome outcome)
        => new(stepName, outcome, RunId: PriorRun, CompletedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

    /// <summary>
    /// Records-in-DB stub. Returns the records list as-is (caller arranges
    /// DESC order if it matters for the test). The builder's matcher iterates
    /// records and returns the first match excluding the current run.
    /// </summary>
    private sealed class StubRunStore(IReadOnlyList<StubRecord> records) : IRunStore
    {
        public Task<IReadOnlyList<StepResultRecord>> GetStepResultsForCardAsync(
            string cardId, string? stateName, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<StepResultRecord>>(
                records.Select(r => r.ToStepResult(cardId, stateName ?? "Unknown"))
                       .OrderByDescending(r => r.CompletedAtUtc)
                       .ToList());

        public Task<IReadOnlyList<StepResultRecord>> GetLatestRunStepResultsAsync(
            string cardId, string stateName, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<StepResultRecord>>([]);

        public Task CreateRunAsync(RunRecord run, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunProgressAsync(string runId, int completedSteps, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteRunAsync(string runId, AgentOutcome outcome, string? errorDetail, FailureReason? failureReason, CancellationToken ct) => Task.CompletedTask;
        public Task SaveStepResultAsync(StepResultRecord result, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunEstimateAsync(string runId, double estimate, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunSessionStartupMsAsync(string runId, int startupMs, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateCandidateEvaluationAsync(
            string runId, Guid candidateGroupId, int candidateIndex,
            bool selected, decimal? qualityScore, string? evaluatorReasoning,
            CancellationToken ct) => Task.CompletedTask;
    }

    private sealed record StubRecord(
        string StepName,
        AgentOutcome Outcome,
        string RunId,
        DateTimeOffset CompletedAt)
    {
        public StepResultRecord ToStepResult(string cardId, string stateName)
            => new(
                RunId: RunId,
                CardId: cardId,
                StateName: stateName,
                StepName: StepName,
                StepIndex: 0,
                Role: "stub",
                Model: "stub",
                Outcome: Outcome,
                Summary: null,
                Detail: null,
                ReferenceContent: null,
                ConversationLog: null,
                Questions: null,
                RequestedSteps: null,
                StartedAtUtc: CompletedAt.AddSeconds(-30),
                CompletedAtUtc: CompletedAt,
                SessionExecMs: null,
                Provider: "stub");
    }
}
