namespace TaskBoard.Worker.Clients;

/// <summary>
/// Configuration for running the Claude CLI inside Docker against a local
/// Anthropic-compatible llama.cpp server (typically Qwen3.6 served by the
/// sibling <c>local-llm</c> compose project). Provider key:
/// <c>docker-claude-qwen</c>.
/// </summary>
/// <remarks>
/// Sits next to <see cref="DockerClaudeAgentOptions"/> (real Anthropic) and
/// <see cref="DockerOpenCodeAgentOptions"/> (OpenCode → Qwen) so all three can
/// be registered simultaneously and pitted against each other through the
/// candidate-evaluation feature.
///
/// <para>The CLI itself is identical to the real-Anthropic case — same
/// invocation, same NDJSON parsing, same <c>--json-schema</c> behaviour. The
/// difference is purely environmental: env vars redirect the CLI to the local
/// llama-server proxy, and the credential staging step writes a synthetic
/// <c>~/.claude</c> directory instead of copying real keys from the host.</para>
/// </remarks>
public sealed class DockerClaudeQwenAgentOptions : DockerAgentOptionsBase
{
    /// <summary>Configuration section: <c>DockerAgents:ClaudeQwen</c>.</summary>
    public const string SectionName = "DockerAgents:ClaudeQwen";

    public DockerClaudeQwenAgentOptions()
    {
        // Reuse the existing Claude sandbox image — the CLI binary is already
        // installed and the Qwen targeting is purely runtime env-var work.
        ImageName = "aiboard-agent-sandbox:latest";
        // Must attach to llm-net so the container resolves `llama-server` via
        // its bridge-network DNS name.
        NetworkMode = "llm-net";
        // Distinct prefix so the orphan-detection log can tell at a glance
        // whether a leftover container came from real-Anthropic, Qwen-target
        // Claude, or OpenCode runs. Still starts with `aiboard-` so the
        // existing prefix filter still catches it.
        ContainerNamePrefix = "aiboard-cq";
        // TimeoutSeconds / InactivityTimeoutSeconds inherit from the base
        // (7200 / 1200). Same rationale as DockerOpenCodeAgentOptions — the
        // inactivity timer catches stuck runs, the hard cap is the outer bound.
    }

    /// <summary>
    /// Bare host:port of the local llama.cpp proxy (no <c>/v1</c> suffix —
    /// Claude CLI's Anthropic Messages adapter appends <c>/v1/messages</c>
    /// itself). Default <c>http://llama-server:8080</c> matches the service
    /// DNS name on the <c>llm-net</c> bridge network.
    /// </summary>
    public string ProviderBaseUrl { get; set; } = "http://llama-server:8080";

    /// <summary>
    /// Auth token sent to the local server. llama.cpp validates nothing,
    /// so any non-empty string works. Never commit a real secret here —
    /// this is a local-only dummy value.
    /// </summary>
    public string AuthToken { get; set; } = "local";

    /// <summary>
    /// Default model alias the templated <c>~/.claude/settings.json</c> selects
    /// when a workflow role doesn't pin one. Two virtual aliases are typically
    /// available on the proxy: <c>qwen3.6-35b-a3b</c> (no thinking — fast tool
    /// loops) and <c>qwen3.6-35b-a3b-think</c> (thinking — synthesis). The
    /// executor honours <see cref="AgentExecutionContext.Model"/> when non-empty
    /// so individual roles can pick either variant; this field is the fallback.
    /// </summary>
    public string ModelName { get; set; } = "qwen3.6-35b-a3b";

    /// <summary>
    /// Mount point inside the container for host-side system prompt files (read-only).
    /// Mirrors <see cref="DockerClaudeAgentOptions.PromptMountPoint"/>.
    /// </summary>
    public string PromptMountPoint { get; set; } = "/mnt/aiboard/prompts";

    /// <summary>
    /// Per-call cost ceiling forwarded as <c>--max-budget-usd</c> to Claude
    /// CLI. Generous default since the local server is free; the flag is still
    /// set so we exercise the same code path as real-Anthropic runs and would
    /// notice if the CLI ever rejected the value.
    /// </summary>
    public decimal MaxBudgetUsd { get; set; } = 50.00m;

    /// <summary>
    /// When true, sets <c>CLAUDE_CODE_ATTRIBUTION_HEADER=0</c> inside the
    /// container so Claude CLI omits the per-request attribution header that
    /// would otherwise vary across runs and bust llama.cpp's prefix cache.
    /// Recommended on per the local-llm/Qwen-3.6.md model card.
    /// </summary>
    public bool DisableAttributionHeader { get; set; } = true;

    /// <summary>
    /// When true, sets <c>CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1</c> so
    /// Claude CLI suppresses telemetry and feature-flag pings. Both reduce
    /// per-turn latency and avoid spurious requests to api.anthropic.com that
    /// would 403 when the CLI is pointed at a local proxy.
    /// </summary>
    public bool DisableNonessentialTraffic { get; set; } = true;

    /// <summary>
    /// Optional extra patterns for rate-limit detection. The local llama.cpp
    /// server effectively never rate-limits, but an upstream proxy or shared
    /// deployment may surface text the default Claude detector misses. Merged
    /// with <see cref="CliRateLimitDetector.ClaudePatterns"/>.
    /// </summary>
    public List<string> RateLimitPatterns { get; set; } = new();
}
