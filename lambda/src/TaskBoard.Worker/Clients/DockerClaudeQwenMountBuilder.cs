using System.Text.Json;

namespace TaskBoard.Worker.Clients;

/// <summary>
/// Builds Docker volume mounts and environment variables for running the Claude
/// CLI inside a container against a local Anthropic-compatible llama.cpp proxy
/// (typically Qwen3.6 served by the sibling <c>local-llm</c> project). Used by
/// <see cref="DockerClaudeQwenAgentExecutor"/>.
/// </summary>
/// <remarks>
/// Differs from <see cref="DockerClaudeMountBuilder"/> in two material ways:
/// <list type="bullet">
///   <item>No host credential staging — the proxy accepts a dummy token, so
///         we skip copying anything from <c>~/.claude</c> on the host.</item>
///   <item>Materialises a synthetic <c>~/.claude/settings.json</c> in a temp
///         dir (mounted RW at <c>/home/agent/.claude</c>) containing
///         <c>hasCompletedOnboarding: true</c> plus the env-var overrides per
///         the local-llm/Qwen-3.6.md model card. A dummy
///         <c>credentials.json</c> sits beside it so the CLI's first-run
///         credential probe finds something.</item>
/// </list>
/// Connection routing is also forwarded via docker <c>-e</c> env vars (belt
/// and braces) so the CLI honours the override regardless of which path it
/// reads first.
/// </remarks>
public sealed class DockerClaudeQwenMountBuilder(ILogger<DockerClaudeQwenMountBuilder> logger)
    : DockerMountBuilderBase
{
    /// <summary>Container path where the synthetic <c>.claude</c> directory is mounted.</summary>
    public const string ClaudeConfigMountPoint = "/home/agent/.claude";

    /// <summary>
    /// Builds the mount + env context.
    /// </summary>
    /// <param name="modelOverride">
    /// Per-call model alias (typically <see cref="AgentExecutionContext.Model"/>)
    /// that overrides <see cref="DockerClaudeQwenAgentOptions.ModelName"/> when
    /// non-empty. Maps to the <c>ANTHROPIC_MODEL</c> env var so a workflow role
    /// can pick between the no-think and <c>-think</c> Qwen aliases per step
    /// without restarting the worker.
    /// </param>
    public async Task<DockerMountContext> BuildAsync(
        string worktreePath,
        DockerClaudeQwenAgentOptions options,
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

        // Synthetic Claude config dir. Two files:
        //   settings.json     — bypasses onboarding, embeds env-var overrides
        //                       (some Claude CLI versions read these here, others
        //                       require docker -e — set both so we don't care).
        //   credentials.json  — dummy entry so the CLI's credential probe
        //                       doesn't complain about a missing file.
        var configDir = await StageClaudeConfigDirAsync(
            options, effectiveModel, cancellationToken);
        tempDirs.Add(configDir);

        mounts.Add(new DockerMount
        {
            HostPath = NormalizeHostPath(configDir),
            ContainerPath = ClaudeConfigMountPoint,
            // RW so the CLI can write its session-env / cache state without
            // failing — the host directory is throwaway and disposed with the
            // mount context.
            ReadOnly = false,
        });

        var envVars = new Dictionary<string, string>
        {
            // Lets git read commands proceed against the RO base .git mount.
            ["GIT_OPTIONAL_LOCKS"] = "0",
            // Redirect Claude CLI to the local proxy. Bare host:port; the
            // Anthropic Messages adapter inside the CLI appends /v1/messages.
            ["ANTHROPIC_BASE_URL"] = options.ProviderBaseUrl,
            // llama.cpp accepts any non-empty token. Setting both this and
            // ANTHROPIC_API_KEY covers CLI versions that check either name.
            ["ANTHROPIC_AUTH_TOKEN"] = options.AuthToken,
            ["ANTHROPIC_API_KEY"] = options.AuthToken,
            // Default model selection. Each docker run gets a fresh env so this
            // can change per call.
            ["ANTHROPIC_MODEL"] = effectiveModel,
        };

        // Cache-friendliness toggles per the local-llm/Qwen-3.6.md card. The
        // attribution header changes per request and busts llama.cpp's prefix
        // cache; nonessential traffic produces requests api.anthropic.com would
        // 200 but the local proxy will 404 (noisy, no functional impact).
        if (options.DisableAttributionHeader)
            envVars["CLAUDE_CODE_ATTRIBUTION_HEADER"] = "0";
        if (options.DisableNonessentialTraffic)
            envVars["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";

        return new DockerMountContext(mounts, envVars, pathMap, tempFiles, tempDirs);
    }

    /// <summary>
    /// Materialises a per-run synthetic <c>~/.claude</c> directory containing
    /// <c>settings.json</c> (with the env-var overrides + onboarding bypass)
    /// and a dummy <c>credentials.json</c>. Returned path is added to the
    /// mount context's <c>tempDirs</c> so it is deleted with the context.
    /// </summary>
    private static async Task<string> StageClaudeConfigDirAsync(
        DockerClaudeQwenAgentOptions options,
        string effectiveModel,
        CancellationToken ct)
    {
        var dir = Path.Combine(
            Path.GetTempPath(),
            $"aiboard-cq-claude-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);

        // settings.json — the env-vars block here is a CLI convenience; the
        // docker -e injection above is what makes it work in practice. We
        // include it so the configured state is visible to anyone who docker
        // exec's into the container for diagnosis.
        var envSection = new Dictionary<string, string>
        {
            ["ANTHROPIC_BASE_URL"] = options.ProviderBaseUrl,
            ["ANTHROPIC_AUTH_TOKEN"] = options.AuthToken,
            ["ANTHROPIC_MODEL"] = effectiveModel,
        };
        if (options.DisableAttributionHeader)
            envSection["CLAUDE_CODE_ATTRIBUTION_HEADER"] = "0";
        if (options.DisableNonessentialTraffic)
            envSection["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";

        var settings = new
        {
            env = envSection,
            hasCompletedOnboarding = true,
        };
        await File.WriteAllTextAsync(
            Path.Combine(dir, "settings.json"),
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }),
            ct);

        // Dummy credentials so the CLI's startup probe finds a file. The env-var
        // route takes precedence; this is just to silence the probe.
        var credentials = new { anthropicApiKey = options.AuthToken };
        await File.WriteAllTextAsync(
            Path.Combine(dir, "credentials.json"),
            JsonSerializer.Serialize(credentials),
            ct);

        return dir;
    }
}
