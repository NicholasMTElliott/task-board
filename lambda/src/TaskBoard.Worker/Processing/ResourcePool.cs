using Microsoft.Extensions.Options;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Concurrency caps for named external resources (e.g. a single local llama.cpp
/// server shared by multiple providers). Providers declare which resources they
/// need; the pool serialises access so two candidates from different providers
/// can't both hammer the same backend simultaneously.
/// </summary>
/// <remarks>
/// Use case: <c>docker-opencode</c> and <c>docker-claude-qwen</c> both run
/// against the same local llama.cpp server on <c>llm-net</c>. Without
/// serialisation, parallel-by-provider candidate execution lets both hit the
/// proxy at once — the second request blocks for minutes waiting for the first
/// to finish, and the inactivity timer fires before any tokens stream back.
/// Declare a <c>local-llm</c> resource with <c>MaxConcurrent = 1</c> and tag
/// both providers; the pool turns "parallel across providers" into "parallel
/// across distinct backends" without the caller having to reason about it.
/// </remarks>
public interface IResourcePool
{
    /// <summary>
    /// Acquires a slot on every resource the given provider declares. Returns
    /// an <see cref="IAsyncDisposable"/> whose disposal releases all acquired
    /// slots in reverse order. Returns a no-op lease when the provider has no
    /// declared resources, so calls are safe even when the pool is unused.
    /// </summary>
    Task<IAsyncDisposable> AcquireAsync(string providerKey, CancellationToken ct);
}

public sealed class ResourcePoolOptions
{
    public const string SectionName = "ResourcePool";

    /// <summary>Resource name → max-concurrent slot count.</summary>
    public Dictionary<string, ResourcePoolDefinition> Pools { get; init; } = new();

    /// <summary>Provider key (e.g. "docker-opencode") → list of resource names it needs.</summary>
    public Dictionary<string, List<string>> ProviderResources { get; init; } = new();
}

public sealed class ResourcePoolDefinition
{
    public int MaxConcurrent { get; init; } = int.MaxValue;
}

public sealed class ResourcePool : IResourcePool, IDisposable
{
    private readonly Dictionary<string, SemaphoreSlim> _semaphores;
    private readonly Dictionary<string, IReadOnlyList<string>> _providerResources;
    private readonly ILogger<ResourcePool> _logger;
    private bool _disposed;

    public ResourcePool(IOptions<ResourcePoolOptions> options, ILogger<ResourcePool> logger)
    {
        _logger = logger;
        var opts = options.Value;

        _semaphores = new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, def) in opts.Pools)
        {
            var max = def.MaxConcurrent <= 0 ? 1 : def.MaxConcurrent;
            _semaphores[name] = new SemaphoreSlim(max, max);
            _logger.LogInformation(
                "ResourcePool registered '{Name}' with MaxConcurrent={Max}", name, max);
        }

        _providerResources = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (provider, resources) in opts.ProviderResources)
        {
            // Filter to known resources, sorted alphabetically to prevent
            // deadlock when multiple providers acquire overlapping resource sets
            // in different orders.
            var filtered = resources
                .Where(r => _semaphores.ContainsKey(r))
                .OrderBy(r => r, StringComparer.Ordinal)
                .ToList();

            var unknown = resources.Except(filtered, StringComparer.OrdinalIgnoreCase).ToList();
            if (unknown.Count > 0)
            {
                _logger.LogWarning(
                    "Provider '{Provider}' references unknown resource(s): {Unknown}. " +
                    "These will be ignored; check ResourcePool:Pools for typos.",
                    provider, string.Join(", ", unknown));
            }

            if (filtered.Count > 0)
            {
                _providerResources[provider] = filtered;
                _logger.LogInformation(
                    "ResourcePool: provider '{Provider}' will acquire {Resources}",
                    provider, string.Join(" + ", filtered));
            }
        }
    }

    public async Task<IAsyncDisposable> AcquireAsync(string providerKey, CancellationToken ct)
    {
        if (!_providerResources.TryGetValue(providerKey, out var resources)
            || resources.Count == 0)
        {
            return NoopLease.Instance;
        }

        var acquired = new List<SemaphoreSlim>(resources.Count);
        try
        {
            foreach (var name in resources)
            {
                var sem = _semaphores[name];
                _logger.LogDebug(
                    "Provider {Provider} waiting for resource '{Resource}' ({Available} slot(s) available)",
                    providerKey, name, sem.CurrentCount);
                await sem.WaitAsync(ct);
                acquired.Add(sem);
                _logger.LogDebug(
                    "Provider {Provider} acquired '{Resource}'", providerKey, name);
            }
            return new Lease(acquired, providerKey, _logger);
        }
        catch
        {
            // Release any partial acquisitions before propagating.
            for (int i = acquired.Count - 1; i >= 0; i--)
            {
                acquired[i].Release();
            }
            throw;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var sem in _semaphores.Values)
        {
            sem.Dispose();
        }
    }

    private sealed class NoopLease : IAsyncDisposable
    {
        public static readonly NoopLease Instance = new();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Lease(
        List<SemaphoreSlim> acquired, string providerKey, ILogger logger) : IAsyncDisposable
    {
        // Atomic 0/1 flag: 0 = not yet released, 1 = released. Interlocked
        // CompareExchange guarantees only one caller wins the release race even
        // if `await using` is bypassed and DisposeAsync gets invoked concurrently
        // (e.g. explicit Dispose + exception unwinder both racing). A plain bool
        // here would let two callers both see false and both call Release(),
        // double-decrementing the semaphore and corrupting the cap.
        private int _released;

        public ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _released, 1, 0) != 0)
                return ValueTask.CompletedTask;
            // Release in reverse order so the most-recently-acquired (innermost)
            // is freed first. Symmetric with the acquisition order on success.
            for (int i = acquired.Count - 1; i >= 0; i--)
            {
                try { acquired[i].Release(); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex,
                        "Failed to release resource semaphore #{Index} for provider {Provider}",
                        i, providerKey);
                }
            }
            return ValueTask.CompletedTask;
        }
    }
}
