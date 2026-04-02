namespace TaskBoard.Worker.Models;

/// <summary>
/// Well-known transition keys used in workflow state machine configuration.
/// These correspond to keys in <see cref="WorkflowState.Transitions"/>.
/// </summary>
internal static class TransitionKeys
{
    public const string InProgress = "IN_PROGRESS";
    public const string Complete = "COMPLETE";
    public const string NeedsInfo = "NEEDS_INFO";
    public const string Error = "ERROR";
    public const string GateFail = "GATE_FAIL";
    public const string MergeConflict = "MERGE_CONFLICT";
}

/// <summary>
/// Well-known gate type values for <see cref="WorkflowState.GateType"/>.
/// </summary>
internal static class GateTypes
{
    public const string AgentRun = "agent_run";
    public const string SystemMerge = "system_merge";
    public const string InProgress = "in_progress";
    public const string ManualGate = "manual_gate";
    public const string ManualEntry = "manual_entry";
    public const string Holding = "holding";
    public const string Terminal = "terminal";
}
