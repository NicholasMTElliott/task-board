using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Pin tests for <see cref="RerunCacheGate"/>. Exercises the four cache
/// outcomes in isolation: no prior, hash mismatch (input drift), section
/// drift, and full hit. Operator-comment classification is exercised via
/// the agent-vs-operator marker check — only operator comments contribute
/// to the input hash.
/// </summary>
public class RerunCacheGateTests
{
    private static readonly NullLogger<RerunCacheGate> _log = NullLogger<RerunCacheGate>.Instance;

    private static WorkflowState State() =>
        new(
            Name: "Ready for Design",
            Role: null,
            GateType: GateTypes.AgentRun,
            TaskPrompt: null,
            Transitions: new());

    private static WorkflowStep Step() =>
        new(Name: "create_design", Role: "senior_engineer", TaskPromptFile: "prompts/design.md");

    private static WorkflowRole Role() =>
        new(Model: "claude-opus-4-6", SystemPrompt: "", Sections: [], SystemPromptFile: "prompts/sr.md", Provider: "claude-cli");

    [Fact]
    public async Task NoPriorComplete_ReturnsMiss()
    {
        var store = new RecordingRunStore { Prior = null };
        var gate = new RerunCacheGate(store, _log);

        var result = await gate.EvaluateAsync(
            cardId: "42",
            cardBody: "operator content",
            state: State(),
            step: Step(),
            role: Role(),
            stepConfigJson: "{}",
            systemPromptContents: "sys",
            taskPromptContents: "task",
            existingComments: [],
            priorSectionOutputHashes: [],
            ct: CancellationToken.None);

        Assert.False(result.IsHit);
        Assert.NotNull(result.CurrentInputHash);
        Assert.Null(result.Source);
    }

    [Fact]
    public async Task HashMatch_AndSectionMatch_ReturnsHit()
    {
        // Simulate a prior run that captured input_hash X and
        // section_output_hash Y. On re-entry with the same body and same
        // inputs, the gate should return a hit.
        var body = BuildBodyWithSection("create_design", "## Design\nbody.");
        var sectionHash = RerunHashBuilder.ComputeSectionHash(body, "create_design");
        Assert.NotNull(sectionHash);

        // Compute what the gate WILL produce for these inputs so we can
        // pre-populate the prior with that exact hash. Using the same call
        // shape as the gate uses internally.
        var inputs = new InputHashInputs(
            OperatorAuthoredDescription: ExtractOperatorPrefix(body),
            OperatorComments: [],
            PriorSectionOutputHashes: [],
            StepConfigJson: "{}",
            SystemPromptContents: "sys",
            TaskPromptContents: "task");
        var expectedHash = RerunHashBuilder.ComputeInputHash(inputs);

        var store = new RecordingRunStore
        {
            Prior = new CacheCandidateRecord(
                Id: Guid.NewGuid(),
                RunId: "prior-run-1",
                CompletedAtUtc: DateTimeOffset.UtcNow.AddHours(-1),
                InputHash: expectedHash,
                SectionOutputHash: sectionHash,
                OutputSummary: "prior summary",
                Detail: null),
        };
        var gate = new RerunCacheGate(store, _log);

        var result = await gate.EvaluateAsync(
            cardId: "42", cardBody: body,
            state: State(), step: Step(), role: Role(),
            stepConfigJson: "{}",
            systemPromptContents: "sys", taskPromptContents: "task",
            existingComments: [],
            priorSectionOutputHashes: [],
            ct: CancellationToken.None);

        Assert.True(result.IsHit);
        Assert.NotNull(result.Source);
        Assert.Equal("prior-run-1", result.Source!.RunId);
    }

    [Fact]
    public async Task InputHashMismatch_ReturnsMiss()
    {
        // Operator edited the description, changing the operator-authored
        // prefix → the input hash differs from the prior run's. Cache miss.
        var body = "OPERATOR EDITED VERSION";
        var store = new RecordingRunStore
        {
            Prior = new CacheCandidateRecord(
                Id: Guid.NewGuid(),
                RunId: "prior-run-1",
                CompletedAtUtc: DateTimeOffset.UtcNow.AddHours(-1),
                InputHash: "deadbeefdeadbeef" + new string('0', 48),  // 64-char hex but mismatched
                SectionOutputHash: null,
                OutputSummary: null,
                Detail: null),
        };
        var gate = new RerunCacheGate(store, _log);

        var result = await gate.EvaluateAsync(
            cardId: "42", cardBody: body,
            state: State(), step: Step(), role: Role(),
            stepConfigJson: "{}",
            systemPromptContents: "sys", taskPromptContents: "task",
            existingComments: [],
            priorSectionOutputHashes: [],
            ct: CancellationToken.None);

        Assert.False(result.IsHit);
        Assert.Null(result.Source);
    }

    [Fact]
    public async Task SectionDrift_ReturnsMiss()
    {
        // The input hash matches (operator content unchanged), but the
        // step's section in the card body has been edited externally so its
        // section_output_hash differs from the prior. Cache must miss for
        // the owning step (so its work re-runs against the edited section).
        var body = BuildBodyWithSection("create_design", "## Design\nDRIFTED content.");
        var sectionHash = RerunHashBuilder.ComputeSectionHash(body, "create_design");

        var inputs = new InputHashInputs(
            OperatorAuthoredDescription: ExtractOperatorPrefix(body),
            OperatorComments: [],
            PriorSectionOutputHashes: [],
            StepConfigJson: "{}",
            SystemPromptContents: "sys",
            TaskPromptContents: "task");
        var matchingInputHash = RerunHashBuilder.ComputeInputHash(inputs);

        var store = new RecordingRunStore
        {
            Prior = new CacheCandidateRecord(
                Id: Guid.NewGuid(),
                RunId: "prior-run-1",
                CompletedAtUtc: DateTimeOffset.UtcNow.AddHours(-1),
                InputHash: matchingInputHash,
                SectionOutputHash: "0000" + new string('0', 60),  // mismatch
                OutputSummary: null,
                Detail: null),
        };
        var gate = new RerunCacheGate(store, _log);

        var result = await gate.EvaluateAsync(
            cardId: "42", cardBody: body,
            state: State(), step: Step(), role: Role(),
            stepConfigJson: "{}",
            systemPromptContents: "sys", taskPromptContents: "task",
            existingComments: [],
            priorSectionOutputHashes: [],
            ct: CancellationToken.None);

        Assert.False(result.IsHit);
        Assert.NotNull(result.CurrentSectionOutputHash);
        // Sanity: the gate's computed section hash matches what the test
        // pre-computed (so the cache decision is grounded in the same input).
        Assert.Equal(sectionHash, result.CurrentSectionOutputHash);
    }

    [Fact]
    public async Task OperatorComments_IncludedInHash()
    {
        // The hash differs based on operator-comment set: one comment vs. two
        // → different hashes. Agent comments (with aiboard-log marker) are
        // filtered out — only operator comments contribute.
        var body = "operator content";
        var store = new RecordingRunStore { Prior = null };
        var gate = new RerunCacheGate(store, _log);

        var oneComment = await gate.EvaluateAsync(
            cardId: "42", cardBody: body,
            state: State(), step: Step(), role: Role(),
            stepConfigJson: "{}",
            systemPromptContents: "sys", taskPromptContents: "task",
            existingComments: [
                new CardComment("op", "first operator comment", DateTimeOffset.UtcNow.AddMinutes(-2)),
            ],
            priorSectionOutputHashes: [],
            ct: CancellationToken.None);

        var twoComments = await gate.EvaluateAsync(
            cardId: "42", cardBody: body,
            state: State(), step: Step(), role: Role(),
            stepConfigJson: "{}",
            systemPromptContents: "sys", taskPromptContents: "task",
            existingComments: [
                new CardComment("op", "first operator comment", DateTimeOffset.UtcNow.AddMinutes(-2)),
                new CardComment("op", "second operator comment", DateTimeOffset.UtcNow.AddMinutes(-1)),
            ],
            priorSectionOutputHashes: [],
            ct: CancellationToken.None);

        Assert.NotEqual(oneComment.CurrentInputHash, twoComments.CurrentInputHash);
    }

    [Fact]
    public async Task AgentMarkedComments_ExcludedFromHash()
    {
        // Operator comment + agent comment with aiboard-log marker → only
        // the operator comment contributes to the hash.
        var body = "operator content";
        var store = new RecordingRunStore { Prior = null };
        var gate = new RerunCacheGate(store, _log);

        var withAgent = await gate.EvaluateAsync(
            cardId: "42", cardBody: body,
            state: State(), step: Step(), role: Role(),
            stepConfigJson: "{}",
            systemPromptContents: "sys", taskPromptContents: "task",
            existingComments: [
                new CardComment("op", "operator note", DateTimeOffset.UtcNow.AddMinutes(-2)),
                new CardComment("bot", "<!-- aiboard-log kind:step -->\nagent step output", DateTimeOffset.UtcNow.AddMinutes(-1)),
            ],
            priorSectionOutputHashes: [],
            ct: CancellationToken.None);

        var withoutAgent = await gate.EvaluateAsync(
            cardId: "42", cardBody: body,
            state: State(), step: Step(), role: Role(),
            stepConfigJson: "{}",
            systemPromptContents: "sys", taskPromptContents: "task",
            existingComments: [
                new CardComment("op", "operator note", DateTimeOffset.UtcNow.AddMinutes(-2)),
            ],
            priorSectionOutputHashes: [],
            ct: CancellationToken.None);

        // Same input hash because the agent comment was filtered out.
        Assert.Equal(withAgent.CurrentInputHash, withoutAgent.CurrentInputHash);
    }

    [Fact]
    public void SerializeStepConfig_IncludesProviderModelAndParams()
    {
        // The serialized config drives cache invalidation when operators
        // change the agent's setup. Pin the fields we serialize so a future
        // change that drops one is caught.
        var step = new WorkflowStep(
            Name: "create_design", Role: "senior_engineer",
            TaskPromptFile: "prompts/design.md");
        var role = new WorkflowRole(
            Model: "claude-opus-4-6", SystemPrompt: "", Sections: [],
            SystemPromptFile: "prompts/sr.md", Provider: "claude-cli");
        var stateParams = new Dictionary<string, string> { ["effort"] = "max" };

        var json = RerunCacheGate.SerializeStepConfig(step, role, stateParams);

        Assert.Contains("\"name\":\"create_design\"", json);
        Assert.Contains("\"role\":\"senior_engineer\"", json);
        Assert.Contains("\"taskPromptFile\":\"prompts/design.md\"", json);
        Assert.Contains("\"provider\":\"claude-cli\"", json);
        Assert.Contains("\"model\":\"claude-opus-4-6\"", json);
        Assert.Contains("\"effort\":\"max\"", json);
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private static string BuildBodyWithSection(string stepName, string content) =>
        $"# Story\n\nReqs.\n\n{DescriptionWriter.ManagedStart}\n" +
        $"<!-- step-section:{stepName} -->\n" +
        $"{content}\n" +
        $"<!-- /step-section:{stepName} -->\n" +
        $"{DescriptionWriter.ManagedEnd}\n";

    private static string ExtractOperatorPrefix(string body)
    {
        var idx = body.IndexOf(DescriptionWriter.ManagedStart, StringComparison.Ordinal);
        return idx < 0 ? body : body.Substring(0, idx);
    }

    private sealed class RecordingRunStore : IRunStore
    {
        public CacheCandidateRecord? Prior { get; set; }

        public Task<CacheCandidateRecord?> GetMostRecentCompleteForStepAsync(
            string cardId, string stateName, string stepName, CancellationToken ct)
            => Task.FromResult(Prior);

        // Stubbed out — RerunCacheGate doesn't use any of these.
        public Task CreateRunAsync(RunRecord run, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunProgressAsync(string runId, int completedSteps, CancellationToken ct) => Task.CompletedTask;
        public Task CompleteRunAsync(string runId, AgentOutcome outcome, string? errorDetail, FailureReason? failureReason, CancellationToken ct) => Task.CompletedTask;
        public Task SaveStepResultAsync(StepResultRecord result, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<StepResultRecord>> GetStepResultsForCardAsync(string cardId, string? stateName, CancellationToken ct) => Task.FromResult<IReadOnlyList<StepResultRecord>>([]);
        public Task<IReadOnlyList<StepResultRecord>> GetLatestRunStepResultsAsync(string cardId, string stateName, CancellationToken ct) => Task.FromResult<IReadOnlyList<StepResultRecord>>([]);
        public Task UpdateRunEstimateAsync(string runId, double estimate, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateRunSessionStartupMsAsync(string runId, int startupMs, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateCandidateEvaluationAsync(string runId, Guid candidateGroupId, int candidateIndex, bool selected, decimal? qualityScore, string? evaluatorReasoning, CancellationToken ct) => Task.CompletedTask;
        public Task IncrementRateLimitEventsAsync(string runId, CancellationToken ct) => Task.CompletedTask;
        public Task FlagWinnersRegressedForRunAsync(string runId, CancellationToken ct) => Task.CompletedTask;
        public Task<int> GetStepAttemptCountAsync(string cardId, string stateName, string stepName, CancellationToken ct) => Task.FromResult(0);
    }
}
