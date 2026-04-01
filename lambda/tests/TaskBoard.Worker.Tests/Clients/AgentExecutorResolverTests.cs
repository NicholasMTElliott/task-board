using NSubstitute;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

public class AgentExecutorResolverTests
{
    [Fact]
    public void Resolve_KnownProvider_ReturnsCorrectExecutor()
    {
        var executorA = Substitute.For<IAgentExecutor>();
        var executorB = Substitute.For<IAgentExecutor>();
        var resolver = new AgentExecutorResolver(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-cli"] = executorA,
            ["codex"] = executorB,
        });

        Assert.Same(executorA, resolver.Resolve("claude-cli"));
        Assert.Same(executorB, resolver.Resolve("codex"));
    }

    [Fact]
    public void Resolve_UnknownProvider_ThrowsWithAvailableProviders()
    {
        var executor = Substitute.For<IAgentExecutor>();
        var resolver = new AgentExecutorResolver(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-cli"] = executor,
        });

        var ex = Assert.Throws<InvalidOperationException>(() => resolver.Resolve("unknown"));
        Assert.Contains("unknown", ex.Message);
        Assert.Contains("claude-cli", ex.Message);
    }

    [Fact]
    public void Resolve_CaseInsensitive_ReturnsExecutor()
    {
        var executor = Substitute.For<IAgentExecutor>();
        var resolver = new AgentExecutorResolver(new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-cli"] = executor,
        });

        Assert.Same(executor, resolver.Resolve("Claude-CLI"));
        Assert.Same(executor, resolver.Resolve("CLAUDE-CLI"));
    }

    [Fact]
    public void ForSingleExecutor_AllKnownKeys_ReturnSameExecutor()
    {
        var executor = Substitute.For<IAgentExecutor>();
        var resolver = AgentExecutorResolver.ForSingleExecutor(executor);

        Assert.Same(executor, resolver.Resolve("claude-cli"));
        Assert.Same(executor, resolver.Resolve("codex"));
        Assert.Same(executor, resolver.Resolve("stub"));
    }
}
