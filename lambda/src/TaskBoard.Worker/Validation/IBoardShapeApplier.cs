namespace TaskBoard.Worker.Validation;

/// <summary>
/// Outcome of a single applier action. Idempotent re-runs that find the
/// resource already present surface as <see cref="AlreadyExists"/>.
/// </summary>
public enum ApplyOutcome
{
    Created,
    AlreadyExists,
    Failed,
}

public sealed record ApplyResult(
    ApplyOutcome Outcome,
    string Description,
    string? Error = null);

/// <summary>
/// Provider-specific writer for board-scaffold actions. Mirrors the read-only
/// <see cref="IBoardShapeProbe"/> on the write side.
///
/// <para>
/// v1 surface: create new single-select fields (with options) and create
/// labels. Modifying existing fields (adding options) and creating Status
/// columns are deliberately deferred — the GitHub Projects v2 GraphQL
/// surface for option mutation is delicate and the operator can do it via
/// the project UI in seconds.
/// </para>
/// </summary>
public interface IBoardShapeApplier
{
    /// <summary>True when this provider can actually perform writes.</summary>
    bool CanApply { get; }

    Task<ApplyResult> CreateSingleSelectFieldAsync(
        string boardId,
        string fieldName,
        IReadOnlyList<string> options,
        CancellationToken cancellationToken);

    Task<ApplyResult> CreateLabelAsync(
        string labelName,
        CancellationToken cancellationToken);
}

/// <summary>
/// Returned for stub / Trello / unknown providers — the runner uses
/// <see cref="CanApply"/> to short-circuit before asking for any writes.
/// </summary>
public sealed class NullBoardShapeApplier : IBoardShapeApplier
{
    public bool CanApply => false;

    public Task<ApplyResult> CreateSingleSelectFieldAsync(
        string boardId, string fieldName, IReadOnlyList<string> options, CancellationToken ct)
        => Task.FromResult(new ApplyResult(
            ApplyOutcome.Failed, $"create field '{fieldName}'",
            "no applier available for the current board provider"));

    public Task<ApplyResult> CreateLabelAsync(
        string labelName, CancellationToken ct)
        => Task.FromResult(new ApplyResult(
            ApplyOutcome.Failed, $"create label '{labelName}'",
            "no applier available for the current board provider"));
}
