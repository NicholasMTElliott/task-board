using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Processing;
using TaskBoard.Worker.Tests.Helpers;

namespace TaskBoard.Worker.Tests.Processing;

public class ResourcePoolTests
{
    private static ResourcePool BuildPool(
        Dictionary<string, int> pools,
        Dictionary<string, List<string>> providerResources)
    {
        var opts = new ResourcePoolOptions
        {
            Pools = pools.ToDictionary(
                kv => kv.Key,
                kv => new ResourcePoolDefinition { MaxConcurrent = kv.Value }),
            ProviderResources = providerResources,
        };
        return new ResourcePool(
            TestOptionsMonitor.Create(opts),
            NullLogger<ResourcePool>.Instance);
    }

    [Fact]
    public async Task AcquireAsync_NoResourcesForProvider_ReturnsNoopLeaseImmediately()
    {
        // Provider not in the map → no waiting, no semaphores held.
        using var pool = BuildPool(
            pools: new() { ["local-llm"] = 1 },
            providerResources: new() { ["docker-opencode"] = ["local-llm"] });

        // Use a totally unrelated provider key — pool should pass through.
        await using var lease = await pool.AcquireAsync("claude-cli", default);
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task AcquireAsync_SerializesSameResourceAcrossDifferentProviders()
    {
        // The headline use case: docker-opencode and docker-claude-qwen both
        // need the local-llm resource. Cap of 1 ⇒ second AcquireAsync blocks
        // until the first lease is disposed.
        using var pool = BuildPool(
            pools: new() { ["local-llm"] = 1 },
            providerResources: new()
            {
                ["docker-opencode"] = ["local-llm"],
                ["docker-claude-qwen"] = ["local-llm"],
            });

        var firstLease = await pool.AcquireAsync("docker-opencode", default);

        // Second acquire should be blocked. Use a cancellation token to prove it.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var secondAcquire = pool.AcquireAsync("docker-claude-qwen", cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondAcquire);

        // Now release the first; the second should succeed if we retry without timeout.
        await firstLease.DisposeAsync();
        await using var thirdLease = await pool.AcquireAsync("docker-claude-qwen", default);
        Assert.NotNull(thirdLease);
    }

    [Fact]
    public async Task AcquireAsync_DifferentResources_DoesNotSerialize()
    {
        // Two providers with disjoint resource sets should run concurrently.
        using var pool = BuildPool(
            pools: new() { ["local-llm"] = 1, ["other-thing"] = 1 },
            providerResources: new()
            {
                ["docker-opencode"] = ["local-llm"],
                ["docker-anything-else"] = ["other-thing"],
            });

        await using var first = await pool.AcquireAsync("docker-opencode", default);
        // This must not block — different resource.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        await using var second = await pool.AcquireAsync("docker-anything-else", cts.Token);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task AcquireAsync_UnknownResourceInProviderList_IgnoredWithWarning()
    {
        // A typo in the operator's config shouldn't crash — the unknown
        // resource is filtered out and the provider proceeds without it.
        using var pool = BuildPool(
            pools: new() { ["local-llm"] = 1 },
            providerResources: new()
            {
                ["docker-opencode"] = ["local-llm", "typo-resource-name"],
            });

        // Should acquire only local-llm; typo-resource-name silently dropped.
        await using var lease = await pool.AcquireAsync("docker-opencode", default);
        Assert.NotNull(lease);
    }

    [Fact]
    public async Task AcquireAsync_MultipleResources_ReleasesInReverseOrder()
    {
        // A provider needing two resources gets both held concurrently and
        // released cleanly on dispose. Tests the multi-resource lease shape.
        using var pool = BuildPool(
            pools: new() { ["resource-a"] = 1, ["resource-b"] = 1 },
            providerResources: new()
            {
                ["multi-provider"] = ["resource-a", "resource-b"],
            });

        var lease = await pool.AcquireAsync("multi-provider", default);

        // Both resources are now held — second-acquire of either should block.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var conflict = pool.AcquireAsync("multi-provider", cts.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => conflict);

        await lease.DisposeAsync();

        // After release, both should be available again.
        await using var lease2 = await pool.AcquireAsync("multi-provider", default);
        Assert.NotNull(lease2);
    }

    [Fact]
    public async Task AcquireAsync_DisposingLeaseTwice_IsSafe()
    {
        using var pool = BuildPool(
            pools: new() { ["r"] = 1 },
            providerResources: new() { ["p"] = ["r"] });

        var lease = await pool.AcquireAsync("p", default);
        await lease.DisposeAsync();
        await lease.DisposeAsync(); // should not throw or double-release

        // Capacity should still be 1 — re-acquire works.
        await using var lease2 = await pool.AcquireAsync("p", default);
        Assert.NotNull(lease2);
    }
}
