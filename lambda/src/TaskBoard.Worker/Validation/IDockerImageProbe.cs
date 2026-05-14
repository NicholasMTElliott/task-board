namespace TaskBoard.Worker.Validation;

/// <summary>
/// Result of a single Docker image existence check.
/// </summary>
public sealed record DockerImageCheck(
    string ProviderKey,
    string ImageName,
    bool ExistsLocally,
    string? ProbeError = null);

/// <summary>
/// Probe that checks whether the Docker images referenced by docker-* providers
/// in a workflow are pullable / present locally. Used by ValidationRunner to
/// catch the "workflow validates fine but runtime fails on missing image" case
/// (the reported v0.0.15: docker-opencode candidates registered but
/// aiboard-opencode-sandbox:latest never built).
/// </summary>
public interface IDockerImageProbe
{
    /// <summary>
    /// For each provider key that maps to a docker-* executor, returns whether
    /// that executor's image exists locally. Providers whose image cannot be
    /// determined (unknown to the probe) are omitted.
    /// </summary>
    /// <param name="providerKeys">All distinct provider keys referenced by the
    /// workflow (roles + candidates).</param>
    Task<IReadOnlyList<DockerImageCheck>> CheckAsync(
        IReadOnlySet<string> providerKeys, CancellationToken ct);
}
