using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Clients;

public interface ICrossReferenceResolver
{
    /// <summary>
    /// Parse text for provider-specific card references.
    /// Returns references with OriginalText populated.
    /// </summary>
    Task<IReadOnlyList<CardReference>> ParseTextReferencesAsync(
        string text, CancellationToken cancellationToken);

    /// <summary>
    /// Fetch structured references from the provider API (sub-items, attachments, etc.).
    /// Returns references with Title populated where available.
    /// </summary>
    Task<IReadOnlyList<CardReference>> GetStructuredReferencesAsync(
        string cardId, CancellationToken cancellationToken);
}
