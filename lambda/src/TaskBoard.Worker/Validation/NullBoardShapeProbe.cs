namespace TaskBoard.Worker.Validation;

/// <summary>
/// Probe for providers that can't enumerate board shape (stub, Trello).
/// Returns null so the validator emits an Info and skips board-side checks.
/// </summary>
public sealed class NullBoardShapeProbe : IBoardShapeProbe
{
    public Task<BoardShape?> ProbeAsync(string boardId, CancellationToken ct) =>
        Task.FromResult<BoardShape?>(null);
}
