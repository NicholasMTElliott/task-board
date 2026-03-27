using System.Text.Json.Serialization;

namespace TaskBoard.Worker.Models;

public sealed record WorkflowConfig(
    Dictionary<string, WorkflowState> States,
    Dictionary<string, WorkflowRole> Roles,
    PollingConfig? Polling = null,
    MergeResolutionConfig? MergeResolution = null)
{
    /// <summary>
    /// The directory containing the workflow config file. Set after deserialization.
    /// Used to resolve prompt file paths relative to the config, not the worktree.
    /// </summary>
    [JsonIgnore]
    public string? ConfigDirectory { get; set; }

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

public sealed record WorkflowState(
    string Name,
    string? Role,
    string GateType,
    string? TaskPrompt,
    Dictionary<string, string> Transitions,
    string? GitBehavior = null,
    string? TaskPromptFile = null,
    Dictionary<string, string>? ProviderParams = null,
    bool IncludeInAgentContext = false,
    int PipelineOrder = 0,
    List<WorkflowStep>? Steps = null,
    GateCheckConfig? GateCheck = null)
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
    string? TaskPromptFile = null);

public sealed record WorkflowRole(
    string Model,
    string SystemPrompt,
    List<string> Sections,
    string? SystemPromptFile = null);

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
