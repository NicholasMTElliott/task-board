namespace TaskBoard.Worker.Validation;

public sealed record BoardFieldOption(string Name, string OptionId);

public sealed record BoardField(
    string Name,
    string DataType,
    IReadOnlyList<BoardFieldOption>? Options = null);

/// <summary>
/// Introspected shape of a board — enumerated columns, custom fields (and their options),
/// and labels. Used by ValidationRunner to cross-check workflow config against reality.
/// A null probe result means introspection is unavailable for this provider.
/// </summary>
public sealed record BoardShape(
    IReadOnlyList<string> ColumnNames,
    IReadOnlyList<BoardField> Fields,
    IReadOnlyList<string> Labels);

public interface IBoardShapeProbe
{
    /// <summary>
    /// Returns the live board shape, or null if this provider cannot introspect
    /// (e.g., stub/Trello). Throws on transient failures (auth, network).
    /// </summary>
    Task<BoardShape?> ProbeAsync(string boardId, CancellationToken ct);
}
