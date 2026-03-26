namespace TaskBoard.Worker.Models;

public sealed record CardReference(
    string ReferencedCardId,
    string ReferenceType,
    string? OriginalText,
    string? Title);

public sealed record ReferenceAnnotationContext(
    string TargetCardId,
    IReadOnlyList<CardReference> References,
    IReadOnlyDictionary<string, string> CardIdToFilePath);
