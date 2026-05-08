using System.Text.Json.Serialization;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Models;

// ── Transition action model ──────────────────────────────────────────────────

public sealed record TransitionAction(
    string Type,
    string? Value = null,
    string? Field = null);

public static class ActionTypes
{
    public const string MoveToColumn = "moveToColumn";
    public const string SetField     = "setField";
    public const string ClearField   = "clearField";
    public const string Assign       = "assign";
    public const string Unassign     = "unassign";
    public const string AddLabel        = "addLabel";
    public const string RemoveLabel     = "removeLabel";
    public const string UpdateParentSum        = "updateParentSum";
    public const string CompleteParentIfReady = "completeParentIfReady";
}

[JsonConverter(typeof(TransitionTargetConverter))]
public sealed record TransitionTarget(List<TransitionAction> Actions)
{
    /// <summary>
    /// The target column name (from the first moveToColumn action), or null if no column move.
    /// </summary>
    [JsonIgnore]
    public string? Column => Actions.FirstOrDefault(a => a.Type == ActionTypes.MoveToColumn)?.Value;

    /// <summary>Creates a TransitionTarget with a single moveToColumn action.</summary>
    public static TransitionTarget ForColumn(string column) =>
        new([new TransitionAction(ActionTypes.MoveToColumn, column)]);
}

// ── Card filter model ────────────────────────────────────────────────────────

public sealed record CardFilter(
    string Type,
    string Operator,
    string? Value = null,
    string? Field = null);

public static class FilterTypes
{
    public const string Label    = "label";
    public const string Assignee = "assignee";
    public const string Field    = "field";
}

public static class FilterOperators
{
    public const string Exists     = "exists";
    public const string NotExists  = "notExists";
    public new const string Equals     = "equals";
    public const string NotEquals  = "notEquals";
    public const string IsEmpty    = "isEmpty";
    public const string IsNotEmpty = "isNotEmpty";
}

// ── Card type / generation config ────────────────────────────────────────────

public sealed record CardTypeDefinition(
    string Name,
    string? LabelPrefix = "type",
    List<string>? AllowedChildren = null);

public sealed record GenerationConfig(
    string TargetType,
    string? TargetColumn = null,
    bool LinkToParent = true,
    List<string>? CopyFields = null,
    Dictionary<string, string>? SetFields = null);

// ── Workflow config ───────────────────────────────────────────────────────────

public sealed record EstimationConfig(
    string CalibrationTicketId,
    int CalibrationSize,
    string FieldName = "Estimate",
    List<int>? Scale = null);

public sealed record DependencyPolicy(
    bool Enabled = false,
    List<string>? EnforcedStates = null,
    List<string>? SatisfiedColumns = null,
    bool CommentOnBlocked = true);

/// <summary>
/// Top-level rerun-redesign config block. All fields optional with sensible
/// defaults so existing workflow.json files continue to work unchanged.
/// </summary>
public sealed record RerunConfig(
    /// <summary>null → auto-detect via <c>git symbolic-ref --short refs/remotes/origin/HEAD</c> at startup.</summary>
    string? DefaultBranch = null,
    RerunCommentsConfig? Comments = null,
    RerunDiffConfig? Diff = null);

public sealed record RerunCommentsConfig(
    /// <summary>Map of comment kind (e.g. "step", "dependency_blocked") → "append" / "delete_and_repost" / "upsert". Unrecognised kinds default to append.</summary>
    Dictionary<string, string>? RetentionPolicy = null);

public sealed record RerunDiffConfig(
    /// <summary>Byte threshold above which the gate's diff switches to summary-mode packet. Default 51200 (50 KB).</summary>
    int? SummaryThresholdBytes = null);

public sealed record WorkflowConfig(
    Dictionary<string, WorkflowState> States,
    Dictionary<string, WorkflowRole> Roles,
    PollingConfig? Polling = null,
    MergeResolutionConfig? MergeResolution = null,
    EstimationConfig? Estimation = null,
    Dictionary<string, CardTypeDefinition>? CardTypes = null,
    string? CardTypeField = null,
    DependencyPolicy? DependencyPolicy = null,
    RerunConfig? Rerun = null)
{
    /// <summary>
    /// The directory containing the workflow config file. Set after deserialization.
    /// Used to resolve prompt file paths relative to the config, not the worktree.
    /// </summary>
    [JsonIgnore]
    public string? ConfigDirectory { get; set; }

    /// <summary>
    /// Returns the names of states with gateType "terminal" — useful for excluding
    /// completed items from board queries.
    /// </summary>
    public IReadOnlyList<string> GetTerminalStateNames() =>
        States
            .Where(kvp => string.Equals(kvp.Value.GateType, GateTypes.Terminal, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Key)
            .ToList();

    /// <summary>
    /// Returns the effective board column name for a state.
    /// If the state has an explicit <see cref="WorkflowState.Column"/> set, that value is returned.
    /// Otherwise the dictionary key (state ID) is returned as the fallback, which matches the
    /// existing convention used in workflow.github.json where state keys equal column names.
    /// </summary>
    public string GetEffectiveColumn(string stateId) =>
        States.TryGetValue(stateId, out var state) && state.Column is not null
            ? state.Column
            : stateId;

    /// <summary>
    /// Returns all workflow states whose effective column matches <paramref name="columnName"/>.
    /// Case-insensitive comparison.
    /// </summary>
    public IReadOnlyList<WorkflowState> FindStatesByColumn(string columnName)
    {
        var result = new List<WorkflowState>();
        foreach (var (stateId, state) in States)
        {
            if (string.Equals(GetEffectiveColumn(stateId), columnName, StringComparison.OrdinalIgnoreCase))
                result.Add(state);
        }
        return result;
    }

    /// <summary>
    /// Returns the distinct board column names for states with gateType "terminal".
    /// Use this (instead of <see cref="GetTerminalStateNames"/>) when you need to compare against
    /// a card's <c>ColumnId</c> field, which contains the board column name.
    /// </summary>
    public IReadOnlyList<string> GetTerminalColumnNames() =>
        States
            .Where(kvp => string.Equals(kvp.Value.GateType, GateTypes.Terminal, StringComparison.OrdinalIgnoreCase))
            .Select(kvp => GetEffectiveColumn(kvp.Key))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// Resolves the workflow state for a card by matching the card's column ID against
    /// state effective columns and evaluating state filters.
    ///
    /// Returns null if no state's effective column matches, or if all matching states
    /// fail their filter predicates.
    ///
    /// Throws <see cref="InvalidOperationException"/> if multiple states match after
    /// filter evaluation — this indicates a config error (insufficient filters to disambiguate).
    /// </summary>
    public WorkflowState? ResolveState(BoardCard card)
    {
        var candidates = FindStatesByColumn(card.ColumnId);
        if (candidates.Count == 0)
            return null;

        // Fast path: single candidate with no filters
        if (candidates.Count == 1 && (candidates[0].Filters is null or { Count: 0 }))
            return candidates[0];

        var matches = candidates
            .Where(s => CardFilterEvaluator.PassesAll(card, s.Filters))
            .ToList();

        if (matches.Count == 0)
            return null;

        if (matches.Count == 1)
            return matches[0];

        throw new InvalidOperationException(
            $"Ambiguous state resolution for card '{card.Id}' in column '{card.ColumnId}': " +
            $"{matches.Count} states match after filter evaluation. " +
            "Add more specific filters to disambiguate.");
    }

    /// <summary>
    /// Collects all distinct provider keys required to execute a given state.
    /// Examines: steps, gate check, and optional steps.
    /// Returns empty set for states with no agent requirements (e.g., system_merge, manual_gate).
    /// </summary>
    public HashSet<string> GetRequiredProviders(WorkflowState state)
    {
        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (state.Steps is { Count: > 0 })
        {
            foreach (var step in state.Steps)
            {
                if (Roles.TryGetValue(step.Role, out var role))
                    providers.Add(role.Provider);
            }
        }

        if (state.GateCheck is not null
            && Roles.TryGetValue(state.GateCheck.Role, out var gateRole))
        {
            providers.Add(gateRole.Provider);
        }

        if (state.OptionalSteps is { Count: > 0 })
        {
            foreach (var optStep in state.OptionalSteps)
            {
                if (Roles.TryGetValue(optStep.Role, out var optRole))
                    providers.Add(optRole.Provider);
            }
        }

        return providers;
    }

    /// <summary>
    /// Returns every distinct provider key referenced anywhere in the config —
    /// across <see cref="Roles"/>, every state's steps / gate check / optional
    /// steps, AND every step's <see cref="WorkflowStep.Candidates"/> override.
    /// Differs from <see cref="GetRequiredProviders"/> by including candidate
    /// overrides, which can introduce providers that no role uses (e.g. a
    /// step's role uses <c>claude-cli</c> but a candidate routes to <c>codex</c>).
    /// Used by validation to enumerate every executor whose preconditions
    /// (Docker images, etc.) need to be probed.
    /// </summary>
    public HashSet<string> GetAllReferencedProviders()
    {
        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var role in Roles.Values)
        {
            if (!string.IsNullOrWhiteSpace(role.Provider))
                providers.Add(role.Provider);
        }

        foreach (var state in States.Values)
        {
            if (state.Steps is { Count: > 0 })
            {
                foreach (var step in state.Steps)
                {
                    if (step.Candidates is { Count: > 0 })
                    {
                        foreach (var c in step.Candidates)
                        {
                            if (!string.IsNullOrWhiteSpace(c.Provider))
                                providers.Add(c.Provider);
                        }
                    }
                }
            }
        }

        return providers;
    }

    /// <summary>
    /// Returns a new config with all states normalised (legacy single-step → steps array).
    /// </summary>
    public WorkflowConfig Normalised()
    {
        var normalisedStates = States.ToDictionary(
            kvp => kvp.Key,
            kvp => WorkflowState.Normalise(kvp.Value));
        return this with { States = normalisedStates };
    }
}

public sealed record OptionalStepDefinition(
    string Name,
    string Role,
    string? TaskPrompt = null,
    string? TaskPromptFile = null,
    string Description = "",
    string Triggers = "",
    Dictionary<string, string>? ProviderParams = null);

public sealed record WorkflowState(
    string Name,
    string? Role,
    string GateType,
    string? TaskPrompt,
    Dictionary<string, TransitionTarget> Transitions,
    string? GitBehavior = null,
    string? TaskPromptFile = null,
    Dictionary<string, string>? ProviderParams = null,
    bool IncludeInAgentContext = false,
    int PipelineOrder = 0,
    List<WorkflowStep>? Steps = null,
    GateCheckConfig? GateCheck = null,
    List<OptionalStepDefinition>? OptionalSteps = null,
    List<CardFilter>? Filters = null,
    string? Column = null)
{
    /// <summary>
    /// Normalises a legacy single-step state (top-level Role + TaskPrompt) into
    /// the canonical steps-based model. States that already have Steps or are not
    /// agent_run are returned unchanged.
    /// </summary>
    public static WorkflowState Normalise(WorkflowState raw)
    {
        if (raw.Steps is { Count: > 0 })
            return raw;

        if (raw.Role is null || (raw.TaskPrompt is null && raw.TaskPromptFile is null))
            return raw; // not an agent_run state; leave as-is

        var stepName = raw.Role;
        return raw with
        {
            Steps =
            [
                new WorkflowStep(stepName, raw.Role, raw.TaskPrompt, raw.TaskPromptFile)
            ]
        };
    }
}

public sealed record WorkflowStep(
    string Name,
    string Role,
    string? TaskPrompt = null,
    string? TaskPromptFile = null,
    GenerationConfig? GenerationConfig = null,
    /// <summary>
    /// Legacy single-slot form. When non-empty, the step runs as a parallel
    /// candidate group with the paired <see cref="Evaluator"/>. Equivalent to
    /// a single-element <see cref="Slots"/> list. Cannot be combined with
    /// <see cref="Slots"/>; the validator rejects steps that set both.
    /// </summary>
    List<CandidateOverride>? Candidates = null,
    /// <summary>
    /// Evaluator paired with the legacy <see cref="Candidates"/> field.
    /// Required whenever <see cref="Candidates"/> has 2+ entries.
    /// </summary>
    EvaluatorConfig? Evaluator = null,
    /// <summary>
    /// Ordered fallback chain. Slots are tried sequentially until one produces
    /// a winner: slot 0 first; if it fails, slot 1; etc. Each slot is itself a
    /// parallel candidate group (same shape as the legacy <see cref="Candidates"/>
    /// + <see cref="Evaluator"/> fields). The first slot whose evaluator picks
    /// a winner — or whose single candidate completes, for 1-candidate slots —
    /// produces the step's result. NEEDS_INFO from a winning slot propagates
    /// up and does NOT trigger fallback (the operator answers and re-runs from
    /// slot 0). Cannot be combined with <see cref="Candidates"/>.
    /// </summary>
    List<SlotConfig>? Slots = null,
    /// <summary>
    /// Whether the step is permitted to write its managed step section in the
    /// card description (rerun redesign Problem 2). Default true. Operators
    /// override per-step: gate-check steps set false (verdicts go in
    /// comments only); evaluator steps set false (the evaluator commits the
    /// winning candidate's section, not its own). Main steps and optional
    /// reviewers default true.
    /// </summary>
    bool? WritesDescriptionSection = null)
{
    /// <summary>
    /// Returns the canonical slots list for this step:
    /// <list type="bullet">
    ///   <item><see cref="Slots"/> if set (the new shape).</item>
    ///   <item>A single-element list wrapping the legacy
    ///         <see cref="Candidates"/> + <see cref="Evaluator"/> when those are set.</item>
    ///   <item>Empty for plain single-agent steps.</item>
    /// </list>
    /// Used by the runtime + validator so the rest of the codebase only sees
    /// the slot-list shape regardless of which form the workflow JSON used.
    /// </summary>
    public IReadOnlyList<SlotConfig> GetEffectiveSlots()
    {
        if (Slots is { Count: > 0 })
            return Slots;
        if (Candidates is { Count: > 0 })
            return [new SlotConfig(Candidates, Evaluator)];
        return [];
    }
}

/// <summary>
/// One slot in a step's fallback chain. The candidates inside the slot run in
/// parallel and are scored by the evaluator (when present). A slot with a
/// single candidate may omit the evaluator — the runtime will surface that
/// candidate's result directly. A slot with 2+ candidates requires an evaluator.
/// </summary>
public sealed record SlotConfig(
    List<CandidateOverride> Candidates,
    EvaluatorConfig? Evaluator = null);

/// <summary>
/// Override for a single candidate in a parallel evaluation group. The
/// <see cref="Provider"/> key must resolve to a registered executor at runtime;
/// <see cref="Model"/> and <see cref="ProviderParams"/> default to the role's
/// configured values when omitted.
/// <para>
/// <see cref="Retries"/> + <see cref="RetryOn"/> control in-slot retry behaviour
/// for transient failures. Defaults: 0 retries, retry-on-RATE_LIMIT-or-TIMEOUT
/// when <see cref="Retries"/> &gt; 0. Retries fire in-place (same provider, same
/// slot) before the slot result is decided; they do NOT cross slot boundaries
/// (that's what fallback slots are for).
/// </para>
/// </summary>
public sealed record CandidateOverride(
    string Provider,
    string? Model = null,
    Dictionary<string, string>? ProviderParams = null,
    int Retries = 0,
    List<FailureReason>? RetryOn = null);

/// <summary>
/// Configures the evaluator step that follows a candidate group. The evaluator
/// reads the task plus all candidate outputs and emits a winner index plus
/// per-candidate scores.
/// </summary>
public sealed record EvaluatorConfig(
    string Role,
    string? TaskPromptFile = null,
    string? TaskPrompt = null,
    EvaluatorScoring Scoring = EvaluatorScoring.WinnerWithScores);

/// <summary>
/// How richly the evaluator should score candidates.
/// <para>
/// <c>WinnerOnly</c> — pick a winner; do not record per-candidate scores.
/// </para>
/// <para>
/// <c>WinnerWithScores</c> (default) — pick a winner AND record a 0–10 quality
/// score plus reasoning for every candidate, so the metrics surface includes
/// "how close was it" data.
/// </para>
/// </summary>
/// <remarks>
/// The <see cref="JsonStringEnumConverter"/> attribute makes JSON values like
/// <c>"WinnerWithScores"</c> / <c>"winnerWithScores"</c> / <c>"WINNERONLY"</c>
/// deserialize to the enum (case-insensitive matching is the default for the
/// converter). Without this, <c>System.Text.Json</c> would only accept the
/// numeric ordinal — and a string value would crash with
/// <c>JsonException: The JSON value could not be converted to EvaluatorScoring</c>.
/// All workflow JSON examples in the docs use the string spelling, so this
/// attribute is what makes those examples actually work end-to-end.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum EvaluatorScoring
{
    WinnerOnly,
    WinnerWithScores,
}

public sealed record WorkflowRole(
    string Model,
    string SystemPrompt,
    List<string> Sections,
    string? SystemPromptFile = null,
    string Provider = "claude-cli");

public sealed record PollingConfig(
    string? PriorityFieldName = null,
    List<string>? PriorityOrder = null);

public sealed record MergeResolutionConfig(
    string Role,
    Dictionary<string, string>? ProviderParams = null);

public sealed record GateCheckConfig(
    string Role,
    string? TaskPromptFile = null,
    string? TaskPrompt = null,
    int MaxDiffChars = 50_000,
    int MaxRetries = 2);
