using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Persistent storage for agent run and step result data.
/// Always registered in DI: PgRunStore when DB is configured, NullRunStore otherwise.
/// </summary>
public interface IRunStore
{
    Task CreateRunAsync(RunRecord run, CancellationToken ct);
    Task UpdateRunProgressAsync(string runId, int completedSteps, CancellationToken ct);
    Task CompleteRunAsync(string runId, AgentOutcome outcome, string? errorDetail, CancellationToken ct);
    Task SaveStepResultAsync(StepResultRecord result, CancellationToken ct);
    Task<IReadOnlyList<StepResultRecord>> GetStepResultsForCardAsync(
        string cardId, string? stateName, CancellationToken ct);
    Task<IReadOnlyList<StepResultRecord>> GetLatestRunStepResultsAsync(
        string cardId, string stateName, CancellationToken ct);

    /// <summary>Persists the story-point estimate for an agent run.</summary>
    Task UpdateRunEstimateAsync(string runId, double estimate, CancellationToken ct);
}
