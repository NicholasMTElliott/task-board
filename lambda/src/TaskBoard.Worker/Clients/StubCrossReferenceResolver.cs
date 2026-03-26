using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Clients;

public sealed class StubCrossReferenceResolver : ICrossReferenceResolver
{
    public Task<IReadOnlyList<CardReference>> ParseTextReferencesAsync(
        string text, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CardReference>>([]);

    public Task<IReadOnlyList<CardReference>> GetStructuredReferencesAsync(
        string cardId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<CardReference>>([]);
}
