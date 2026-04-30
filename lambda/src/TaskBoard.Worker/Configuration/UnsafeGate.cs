using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Configuration;

/// <summary>
/// Decides whether the host CLI executors (<c>claude-cli</c>, <c>codex</c>) —
/// which bypass the Docker filesystem sandbox — are permitted to run for a
/// given workflow + execution mode. Pure function, no side effects, so the
/// rules can be unit-tested independently of <c>Program.cs</c>.
/// </summary>
/// <remarks>
/// The gate fires when a workflow references a host CLI provider AND the
/// operator hasn't opted in via <c>--unsafe</c>. Two carve-outs avoid
/// false-positives:
/// <list type="bullet">
///   <item><c>AGENT_EXECUTOR=stub</c> — every provider key maps to
///         <see cref="Clients.StubAgentExecutor"/>; no real CLI runs.</item>
///   <item><c>AGENT_EXECUTOR=docker-claude-cli</c> — workflow string
///         <c>claude-cli</c> is transparently redirected to
///         <see cref="Clients.DockerClaudeAgentExecutor"/> (sandboxed).
///         <c>codex</c> has no equivalent redirect and is still gated.</item>
/// </list>
/// </remarks>
public static class UnsafeGate
{
    /// <summary>
    /// Outcome of evaluating the gate. <see cref="Allow"/> is <c>true</c> when
    /// the run may proceed. When <c>false</c>, <see cref="ErrorMessage"/>
    /// holds the operator-facing rejection text and <see cref="BlockedProviders"/>
    /// lists the offending workflow references (alphabetised).
    /// </summary>
    public sealed record Result(
        bool Allow,
        IReadOnlyList<string> BlockedProviders,
        string? ErrorMessage);

    private static readonly Result AllowEmpty = new(Allow: true, [], null);

    /// <summary>
    /// Evaluates the gate against a loaded workflow and the current
    /// execution-mode flags. Pure: returns the same result for the same
    /// inputs, never logs, never throws.
    /// </summary>
    /// <param name="config">Workflow config (already loaded and normalised).</param>
    /// <param name="unsafeMode">
    /// True when the operator passed <c>--unsafe</c> or set <c>Unsafe=true</c>
    /// in configuration.
    /// </param>
    /// <param name="agentExecutorMode">
    /// The lower-cased value of <c>AgentExecutor</c> configuration. Recognised
    /// special values: <c>stub</c>, <c>docker-claude-cli</c>. Anything else is
    /// treated as production auto-detect mode.
    /// </param>
    public static Result Evaluate(
        WorkflowConfig config,
        bool unsafeMode,
        string? agentExecutorMode)
    {
        ArgumentNullException.ThrowIfNull(config);

        // Carve-out 1: stub mode. Every provider key maps to StubAgentExecutor;
        // no real CLI runs, so there's nothing to sandbox.
        if (string.Equals(agentExecutorMode, "stub", StringComparison.OrdinalIgnoreCase))
            return AllowEmpty;

        // Carve-out 2: explicit operator opt-in.
        if (unsafeMode)
            return AllowEmpty;

        // Build the blocklist of host providers we care about. In docker-claude-cli
        // mode, claude-cli is transparently routed through Docker — drop it from
        // the blocklist so existing workflows referencing claude-cli still work
        // without --unsafe (this is the documented zero-edit migration path).
        // codex stays blocked: there's no docker-codex redirect aliasing.
        var hostCliProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "claude-cli", "codex",
        };
        if (string.Equals(agentExecutorMode, "docker-claude-cli", StringComparison.OrdinalIgnoreCase))
            hostCliProviders.Remove("claude-cli");

        var referenced = config.GetAllReferencedProviders();
        var blocked = referenced
            .Where(hostCliProviders.Contains)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (blocked.Count == 0)
            return AllowEmpty;

        var msg =
            $"Workflow references unsafe (non-sandboxed) provider(s): {string.Join(", ", blocked)}. " +
            "These run host-installed CLIs with full filesystem and credential access — " +
            "the agent can read your SSH keys, modify files outside the worktree, and " +
            "make destructive git changes. Either:\n" +
            "  (a) migrate to sandboxed equivalents in your workflow JSON: " +
            "claude-cli → docker-claude-cli, codex → docker-codex; or\n" +
            "  (b) re-run with --unsafe (or set Unsafe=true in appsettings) to opt in.";
        return new Result(Allow: false, blocked, msg);
    }
}
