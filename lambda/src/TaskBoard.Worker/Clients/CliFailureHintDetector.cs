namespace TaskBoard.Worker.Clients;

/// <summary>
/// Shared stderr-signature detection for CLI-backed agent executors.
/// Maps known substrings in stderr output to short, actionable hints that
/// surface in logs and thrown exception messages so the operator sees the
/// likely root cause alongside the raw stderr dump.
/// </summary>
/// <remarks>
/// Purely advisory — the actual exception still carries raw stderr. Matches
/// are case-insensitive substring matches. Order matters: entries are checked
/// in order and the first match wins, so put specific patterns before generic
/// ones. Bare status codes like "429" are deliberately avoided to prevent
/// false positives on unrelated numeric content.
/// </remarks>
public static class CliFailureHintDetector
{
    public sealed record FailureHint(string Category, string Hint);

    /// <summary>
    /// Codex / OpenAI CLI signatures. Covers authentication, model catalog,
    /// CLI-version drift, schema validation, sandbox policy, quota, and network
    /// categories. Used by <see cref="CodexAgentExecutor"/>.
    /// </summary>
    public static readonly IReadOnlyList<(string Pattern, string Category, string Hint)>
        CodexSignatures = new (string, string, string)[]
    {
        ("OPENAI_API_KEY", "Auth",
            "OPENAI_API_KEY is missing or empty. Set the env var or run 'codex login'."),
        ("not authenticated", "Auth",
            "Codex CLI reports it is not authenticated. Run 'codex login' and verify."),
        ("invalid_api_key", "Auth",
            "OPENAI_API_KEY is rejected by the provider. Rotate the key or re-run 'codex login'."),
        ("401 Unauthorized", "Auth",
            "Provider returned HTTP 401. Re-authenticate via 'codex login' or verify OPENAI_API_KEY."),
        ("403 Forbidden", "Auth",
            "Provider returned HTTP 403. The account may lack access to the requested model."),
        ("model_not_found", "Model",
            "The configured model is unknown to the provider. Check Role.Model against the current Codex model catalog."),
        ("unknown model", "Model",
            "Codex reports an unknown model name. Check Role.Model against the current Codex model catalog."),
        ("unrecognized subcommand", "VersionDrift",
            "Codex CLI rejected a subcommand. The installed Codex version may not support 'exec --json --output-schema'. Check 'codex --version' and compare to the tested baseline."),
        ("unknown command", "VersionDrift",
            "Codex CLI rejected a command. Likely a CLI version change; check 'codex --version'."),
        ("error: unexpected argument", "VersionDrift",
            "Codex CLI rejected a flag. Likely a CLI version change; check 'codex --version'."),
        ("schema validation", "Schema",
            "Codex rejected the output schema. If OutcomeSchemaOpenAI was updated, run Codex manually with the schema file to see the specific validation message."),
        ("sandbox policy", "Sandbox",
            "Codex sandbox policy blocked an operation. Review the role's providerParams (sandbox/fullAuto/yolo) against the action attempted."),
        ("insufficient_quota", "Quota",
            "OpenAI account quota exhausted. Add credits or wait for the billing window to roll over."),
        ("Failed to connect", "Network",
            "Codex CLI could not reach the provider. Check network/firewall; a local proxy may be intercepting."),
        ("timed out", "Network",
            "Provider request timed out. Could be transient load or a network issue."),
    };

    /// <summary>
    /// OpenCode / llama.cpp signatures. Distinct from Codex because the backend
    /// is a local llama.cpp server on the llm-net network, not the OpenAI API —
    /// errors cluster around unreachable host, missing network, and invalid
    /// config rather than auth / quota.
    /// </summary>
    public static readonly IReadOnlyList<(string Pattern, string Category, string Hint)>
        OpenCodeSignatures = new (string, string, string)[]
    {
        ("network llm-net not found", "Network",
            "Docker network 'llm-net' does not exist. Start the local-llm compose project first: `cd ../local-llm && docker compose up -d`."),
        ("could not resolve host", "Network",
            "Sandbox could not resolve the llama-server hostname. Verify the sandbox is attached to llm-net and that the local-llm stack is up."),
        ("connection refused", "Network",
            "llama-server refused the connection. Check `docker ps` for the llama-server container and that it is listening on the configured port."),
        ("no route to host", "Network",
            "No route to llama-server. Likely a network-mode misconfiguration (llm-net not attached) or llama-server is not running."),
        ("provider not found", "Config",
            "OpenCode config references an unknown provider key. Check /home/agent/.config/opencode/opencode.json inside the sandbox image."),
        ("model not found", "Model",
            "llama-server does not have the requested model loaded. Verify OPENCODE_MODEL_NAME matches the model alias in local-llm's docker-compose.yml."),
        ("context length", "Model",
            "Prompt exceeded llama-server's configured --ctx-size. Either trim the prompt or increase --ctx-size in local-llm."),
        ("404", "Config",
            "llama-server returned 404. OpenCode is probably hitting the wrong URL path; verify OPENCODE_PROVIDER_BASE_URL."),
        ("401", "Auth",
            "llama-server returned 401. It should accept any token — verify the baked opencode.json is sending an Authorization header."),
        ("timed out", "Network",
            "Request to llama-server timed out. Either the model is cold-loading a large context (normal on first request) or llama-server is stuck."),
    };

    /// <summary>
    /// Returns the first signature in <paramref name="signatures"/> whose pattern appears
    /// (case-insensitive substring) in <paramref name="stderr"/>, or null if none match.
    /// </summary>
    public static FailureHint? Detect(
        string stderr,
        IReadOnlyList<(string Pattern, string Category, string Hint)> signatures)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return null;

        foreach (var (pattern, category, hint) in signatures)
        {
            if (stderr.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return new FailureHint(category, hint);
        }

        return null;
    }
}
