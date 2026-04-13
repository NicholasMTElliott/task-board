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
    string LabelPrefix = "type",
    List<string>? AllowedChildren = null);

public sealed record GenerationConfig(
    string TargetType,
    string? TargetColumn = null,
    bool LinkToParent = true,
    List<string>? CopyFields = null);

// ── Workflow config ───────────────────────────────────────────────────────────

public sealed record EstimationConfig(
    string CalibrationTicketId,
    int CalibrationSize,
    string FieldName = "Estimate",
    List<int>? Scale = null);

public sealed record WorkflowConfig(
    Dictionary<string, WorkflowState> States,
    Dictionary<string, WorkflowRole> Roles,
    PollingConfig? Polling = null,
    MergeResolutionConfig? MergeResolution = null,
    EstimationConfig? Estimation = null,
    Dictionary<string, CardTypeDefinition>? CardTypes = null)
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
    GenerationConfig? GenerationConfig = null);

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
