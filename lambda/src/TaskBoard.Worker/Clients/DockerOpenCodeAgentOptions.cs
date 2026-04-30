namespace TaskBoard.Worker.Clients;

/// <summary>
/// Configuration for running the OpenCode CLI inside a Docker container
/// (see <see cref="DockerOpenCodeAgentExecutor"/>) against a local
/// Anthropic-compatible llama.cpp server such as the `local-llm` Qwen3.6
/// deployment.
/// </summary>
/// <remarks>
/// Bound from the <c>DockerAgents:OpenCode</c> configuration section. Unlike
/// <see cref="DockerClaudeAgentOptions"/>, there is no credential directory —
/// the local server accepts a dummy token, passed via the
/// <see cref="AuthToken"/> env var to the sandbox entrypoint.
/// </remarks>
public sealed class DockerOpenCodeAgentOptions : DockerAgentOptionsBase
{
    /// <summary>Configuration section: <c>DockerAgents:OpenCode</c>.</summary>
    public const string SectionName = "DockerAgents:OpenCode";

    public DockerOpenCodeAgentOptions()
    {
        // Default image is the OpenCode sandbox built by scripts/build-opencode-sandbox.ps1.
        ImageName = "aiboard-opencode-sandbox:latest";
        // Must be attached to the external llm-net bridge network (created by
        // the `local-llm` compose project) so the container can resolve
        // `llama-server` by DNS name.
        NetworkMode = "llm-net";
        // Short prefix keeps container names readable while preserving the
        // `aiboard-` marker that orphaned-container detection filters on.
        ContainerNamePrefix = "aiboard-oc";
        // TimeoutSeconds / InactivityTimeoutSeconds inherit from the base
        // (7200 / 1200) — the inactivity timer is the relevant signal for
        // local Qwen runs that get stuck mid-explore; the 2h hard cap is
        // the outer bound for genuinely long implementations.
    }

    /// <summary>
    /// Base URL of the OpenAI-compatible API exposed by the local llama.cpp
    /// proxy. Default is <c>http://llama-server:8080/v1</c> — the service DNS
    /// name inside the `llm-net` bridge network managed by the local-llm
    /// project, with the <c>/v1</c> suffix the
    /// <c>@ai-sdk/openai-compatible</c> adapter expects (it appends
    /// <c>/chat/completions</c> to this prefix).
    /// </summary>
    public string ProviderBaseUrl { get; set; } = "http://llama-server:8080/v1";

    /// <summary>
    /// Auth token sent to the local server. llama.cpp validates nothing,
    /// so any non-empty string works. Never commit a real secret here —
    /// this is a local-only dummy value.
    /// </summary>
    public string AuthToken { get; set; } = "local";

    /// <summary>
    /// Default model alias selected by the templated <c>opencode.json</c> when
    /// no per-call override is supplied. Two virtual models are registered in
    /// the sandbox image: <c>qwen3.6-35b-a3b</c> (no thinking, fast — production
    /// default) and <c>qwen3.6-35b-a3b-think</c> (thinking, ~7× tokens — for
    /// design / synthesis roles). The executor honours
    /// <see cref="AgentExecutionContext.Model"/> when non-empty so individual
    /// roles can pick either variant; this field is the fallback.
    /// </summary>
    public string ModelName { get; set; } = "qwen3.6-35b-a3b";

    /// <summary>
    /// Mount point inside the container for host-side system prompt files (read-only).
    /// Mirrors <see cref="DockerClaudeAgentOptions.PromptMountPoint"/>.
    /// </summary>
    public string PromptMountPoint { get; set; } = "/mnt/aiboard/prompts";

    /// <summary>
    /// How many times the executor retries when the CLI output cannot be parsed
    /// as the Agent Contract JSON. After the final failed attempt the executor
    /// returns <c>{outcome: "ERROR"}</c> with the raw output in detail rather
    /// than throwing.
    /// </summary>
    public int MaxRetriesOnMalformedOutput { get; set; } = 2;

    /// <summary>
    /// The CLI subcommand + flag sequence used to invoke OpenCode for a single
    /// headless agent turn. Defaults to <c>["run"]</c>, which reads the prompt
    /// from stdin in OpenCode's current CLI shape. If OpenCode's CLI changes
    /// (or the operator wants to pin a sub-mode), override via config without
    /// recompiling.
    /// </summary>
    /// <remarks>
    /// The executor appends the task prompt via stdin; it does NOT substitute
    /// values into this list. Keep it to flags that apply regardless of the
    /// specific task.
    /// </remarks>
    public List<string> CliArguments { get; set; } = new() { "run" };

    /// <summary>
    /// Optional extra patterns for rate-limit detection. llama.cpp itself
    /// rarely rate-limits (local server, single slot), but an upstream proxy
    /// or shared deployment may surface rate-limit wording the default
    /// OpenCode detector misses. Merged with
    /// <see cref="CliRateLimitDetector.ClaudePatterns"/> (the local server
    /// speaks the Anthropic wire format, so Anthropic-style messages are the
    /// common case).
    /// </summary>
    public List<string> RateLimitPatterns { get; set; } = new();
}
