namespace TaskBoard.Worker.Models;

/// <summary>
/// Represents a single agent run execution envelope (one per AgentRunner.ExecuteAsync invocation).
/// outcome is null while in-progress; set on completion.
/// </summary>
public sealed record RunRecord(
    string RunId,
    string CardId,
    string StateName,
    string AgentIdentity,
    string? GitBranch,
    int TotalSteps,
    int CompletedSteps = 0,
    TaskBoard.Worker.Clients.AgentOutcome? Outcome = null,
    string? ErrorDetail = null,
    FailureReason? FailureReason = null,
    DateTimeOffset? StartedAtUtc = null,
    DateTimeOffset? CompletedAtUtc = null);
