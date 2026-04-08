using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Models;

/// <summary>
/// Represents the result of a single agent step execution.
/// Covers mandatory steps, gate checks (step_name = "gate_check"),
/// and optional steps (step_name = "optional:{name}").
/// </summary>
public sealed record StepResultRecord(
    string RunId,
    string CardId,
    string StateName,
    string StepName,
    int StepIndex,
    string Role,
    string Model,
    AgentOutcome Outcome,
    string? Summary,
    string? Detail,
    string? ReferenceContent,
    string? ConversationLog,
    IReadOnlyList<AgentQuestion>? Questions,
    IReadOnlyList<string>? RequestedSteps,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    /// <summary>
    /// Time in milliseconds taken to execute this step via docker exec inside a reused container.
    /// Null when the step ran via direct docker run (no session) or via a non-Docker executor.
    /// </summary>
    int? SessionExecMs = null);
