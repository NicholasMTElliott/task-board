namespace TaskBoard.Worker.Clients;

/// <summary>
/// Builds Docker volume mount specifications for running the OpenCode CLI
/// inside a container. Extends <see cref="DockerMountBuilderBase"/> with
/// connection env vars for the local llama.cpp server.
/// </summary>
/// <remarks>
/// Produces three bind mounts (all from the base class):
/// <list type="number">
///   <item>Worktree (RW) at <c>/workspace</c>.</item>
///   <item>Base <c>.git</c> directory (RO) at <c>/repo/.git</c>.</item>
///   <item><c>.git</c> file override (RO) so the worktree resolves correctly
///         inside the container.</item>
/// </list>
/// Unlike <see cref="DockerClaudeMountBuilder"/>, there is no credential
/// directory — llama.cpp accepts a dummy token, so connection details are
/// injected as environment variables and templated into the OpenCode config
/// file by the sandbox entrypoint.
/// </remarks>
public sealed class DockerOpenCodeMountBuilder(ILogger<DockerOpenCodeMountBuilder> logger)
    : DockerMountBuilderBase
{
    /// <summary>
    /// Builds a <see cref="DockerMountContext"/> containing volume mounts and
    /// environment variables needed to run the OpenCode CLI against the
    /// configured llama-server.
    /// </summary>
    /// <param name="modelOverride">
    /// Per-call model alias (typically <see cref="AgentExecutionContext.Model"/>)
    /// that overrides <see cref="DockerOpenCodeAgentOptions.ModelName"/> when
    /// non-empty. The sandbox image has both Qwen3.6 variants registered, so
    /// passing either <c>qwen3.6-35b-a3b</c> or <c>qwen3.6-35b-a3b-think</c>
    /// here selects which one OpenCode uses for this run.
    /// </param>
    public async Task<DockerMountContext> BuildAsync(
        string worktreePath,
        DockerOpenCodeAgentOptions options,
        string? modelOverride = null,
        CancellationToken cancellationToken = default)
    {
        var mounts = new List<DockerMount>();
        var tempFiles = new List<string>();
        var tempDirs = new List<string>();
        var pathMap = new List<(string HostPrefix, string ContainerPrefix)>();

        await AddWorkspaceAndGitMountsAsync(
            worktreePath, mounts, tempFiles, pathMap, logger, cancellationToken);

        var effectiveModel = !string.IsNullOrWhiteSpace(modelOverride)
            ? modelOverride
            : options.ModelName;

        var envVars = new Dictionary<string, string>
        {
            // Prevents git from acquiring index locks on read-only operations
            // (base .git mount is read-only).
            ["GIT_OPTIONAL_LOCKS"] = "0",
            // Connection details consumed by the sandbox entrypoint
            // (docker/opencode-sandbox/entrypoint.sh) when templating
            // opencode.json.
            ["OPENCODE_PROVIDER_BASE_URL"] = options.ProviderBaseUrl,
            ["OPENCODE_AUTH_TOKEN"] = options.AuthToken,
            ["OPENCODE_MODEL_NAME"] = effectiveModel,
        };

        return new DockerMountContext(mounts, envVars, pathMap, tempFiles, tempDirs);
    }
}
