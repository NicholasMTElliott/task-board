using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Processing;

public interface IDependencyWaitStore
{
    Task RecordBlockedAsync(
        string cardId,
        IReadOnlyList<CardDependency> unresolvedBlockers,
        string source,
        CancellationToken ct);
}

public sealed class NullDependencyWaitStore : IDependencyWaitStore
{
    public static readonly NullDependencyWaitStore Instance = new();

    private NullDependencyWaitStore() { }

    public Task RecordBlockedAsync(
        string cardId,
        IReadOnlyList<CardDependency> unresolvedBlockers,
        string source,
        CancellationToken ct) => Task.CompletedTask;
}
