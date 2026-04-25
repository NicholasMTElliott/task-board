using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// No-op IRunStore used when no database connection is configured.
/// All write methods complete without doing anything.
/// All read methods return empty collections.
/// This ensures AgentRunner works fully without a database.
/// </summary>
public sealed class NullRunStore : IRunStore
{
    public static readonly NullRunStore Instance = new();

    private NullRunStore() { }

    public Task CreateRunAsync(RunRecord run, CancellationToken ct) => Task.CompletedTask;

    public Task UpdateRunProgressAsync(string runId, int completedSteps, CancellationToken ct)
        => Task.CompletedTask;

    public Task CompleteRunAsync(string runId, AgentOutcome outcome, string? errorDetail, FailureReason? failureReason, CancellationToken ct)
        => Task.CompletedTask;

    public Task SaveStepResultAsync(StepResultRecord result, CancellationToken ct)
        => Task.CompletedTask;

    public Task<IReadOnlyList<StepResultRecord>> GetStepResultsForCardAsync(
        string cardId, string? stateName, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<StepResultRecord>>([]);

    public Task<IReadOnlyList<StepResultRecord>> GetLatestRunStepResultsAsync(
        string cardId, string stateName, CancellationToken ct)
        => Task.FromResult<IReadOnlyList<StepResultRecord>>([]);

    public Task UpdateRunEstimateAsync(string runId, double estimate, CancellationToken ct)
        => Task.CompletedTask;

    public Task UpdateRunSessionStartupMsAsync(string runId, int startupMs, CancellationToken ct)
        => Task.CompletedTask;

    public Task UpdateCandidateEvaluationAsync(
        string runId,
        Guid candidateGroupId,
        int candidateIndex,
        bool selected,
        decimal? qualityScore,
        string? evaluatorReasoning,
        CancellationToken ct)
        => Task.CompletedTask;
}
