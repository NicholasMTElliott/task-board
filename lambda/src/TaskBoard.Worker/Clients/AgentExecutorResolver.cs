namespace TaskBoard.Worker.Clients;

public sealed class AgentExecutorResolver(
    IReadOnlyDictionary<string, IAgentExecutor> executors) : IAgentExecutorResolver
{
    public IReadOnlySet<string> AvailableProviders { get; } =
        new HashSet<string>(executors.Keys, StringComparer.OrdinalIgnoreCase);

    public IAgentExecutor Resolve(string providerKey)
    {
        if (executors.TryGetValue(providerKey, out var executor))
            return executor;

        var available = string.Join(", ", executors.Keys.Order());
        throw new InvalidOperationException(
            $"No agent executor registered for provider '{providerKey}'. Available providers: {available}");
    }

    /// <summary>
    /// Convenience factory for tests: maps all known provider keys to a single executor.
    /// </summary>
    public static AgentExecutorResolver ForSingleExecutor(IAgentExecutor executor) =>
        new(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-cli"] = executor,
            ["codex"] = executor,
            ["stub"] = executor,
            ["docker"] = executor,
        });
}
