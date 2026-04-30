using TaskBoard.Worker.Configuration;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Tests.Configuration;

/// <summary>
/// Unit tests for <see cref="UnsafeGate.Evaluate"/>. Pre-extraction these
/// scenarios lived inside Program.cs's startup block where they couldn't be
/// tested — and a real bug shipped (the gate fired in stub mode and in the
/// docker-claude-cli redirect path, breaking backward compatibility for
/// existing workflows). The extraction makes the four boundary cases trivial
/// to exercise.
/// </summary>
public class UnsafeGateTests
{
    private static WorkflowConfig ConfigWithProviders(params string[] providers)
    {
        var roles = new Dictionary<string, WorkflowRole>();
        for (var i = 0; i < providers.Length; i++)
        {
            roles[$"role_{i}"] = new WorkflowRole(
                Model: "m", SystemPrompt: "p", Sections: new List<string>(),
                Provider: providers[i]);
        }

        var state = new WorkflowState(
            Name: "Test", Role: null, GateType: "agent_run", TaskPrompt: null,
            Transitions: new Dictionary<string, TransitionTarget>());

        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState> { ["test"] = state },
            Roles: roles);
    }

    // ── Carve-out 1: stub mode ───────────────────────────────────────────────

    [Fact]
    public void StubMode_WorkflowReferencesClaudeCli_Allows()
    {
        // Regression guard for B1: pre-extraction the gate fired in stub mode
        // and broke `AGENT_EXECUTOR=stub` runs of workflows referencing
        // claude-cli (a major test-mode regression).
        var config = ConfigWithProviders("claude-cli");

        var result = UnsafeGate.Evaluate(config, unsafeMode: false, agentExecutorMode: "stub");

        Assert.True(result.Allow);
        Assert.Empty(result.BlockedProviders);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public void StubMode_WorkflowReferencesCodex_Allows()
    {
        var config = ConfigWithProviders("codex");

        var result = UnsafeGate.Evaluate(config, unsafeMode: false, agentExecutorMode: "stub");

        Assert.True(result.Allow);
    }

    [Fact]
    public void StubMode_IsCaseInsensitive()
    {
        var config = ConfigWithProviders("claude-cli", "codex");

        var result = UnsafeGate.Evaluate(config, unsafeMode: false, agentExecutorMode: "Stub");

        Assert.True(result.Allow);
    }

    // ── Carve-out 2: docker-claude-cli redirect ──────────────────────────────

    [Fact]
    public void DockerClaudeCliMode_WorkflowReferencesClaudeCli_Allows()
    {
        // Regression guard for B2: in docker-claude-cli mode the resolver
        // transparently maps "claude-cli" → DockerClaudeAgentExecutor, which
        // IS sandboxed. The gate must not refuse this — it would defeat the
        // documented zero-edit migration path.
        var config = ConfigWithProviders("claude-cli");

        var result = UnsafeGate.Evaluate(
            config, unsafeMode: false, agentExecutorMode: "docker-claude-cli");

        Assert.True(result.Allow);
        Assert.Empty(result.BlockedProviders);
    }

    [Fact]
    public void DockerClaudeCliMode_WorkflowReferencesCodex_StillBlocks()
    {
        // codex has no docker-claude-cli redirect — even in docker-claude-cli
        // mode, a workflow referencing codex bypasses the sandbox and must
        // still require --unsafe.
        var config = ConfigWithProviders("codex");

        var result = UnsafeGate.Evaluate(
            config, unsafeMode: false, agentExecutorMode: "docker-claude-cli");

        Assert.False(result.Allow);
        Assert.Equal(new[] { "codex" }, result.BlockedProviders);
        Assert.Contains("codex", result.ErrorMessage!);
    }

    [Fact]
    public void DockerClaudeCliMode_MixedClaudeAndCodex_OnlyCodexBlocks()
    {
        // claude-cli should slip through the redirect carve-out; codex should
        // still be flagged. Regression guard against an over-broad carve-out
        // that drops the entire blocklist when claude-cli is referenced.
        var config = ConfigWithProviders("claude-cli", "codex");

        var result = UnsafeGate.Evaluate(
            config, unsafeMode: false, agentExecutorMode: "docker-claude-cli");

        Assert.False(result.Allow);
        // BlockedProviders is the canonical signal — the human-facing error
        // message always names both providers in the migration hint, so we
        // can't assert on its text.
        Assert.Equal(new[] { "codex" }, result.BlockedProviders);
    }

    [Fact]
    public void DockerClaudeCliMode_IsCaseInsensitive()
    {
        var config = ConfigWithProviders("claude-cli");

        var result = UnsafeGate.Evaluate(
            config, unsafeMode: false, agentExecutorMode: "Docker-Claude-CLI");

        Assert.True(result.Allow);
    }

    // ── Carve-out 3: explicit --unsafe ───────────────────────────────────────

    [Fact]
    public void UnsafeMode_WorkflowReferencesBothHostProviders_Allows()
    {
        var config = ConfigWithProviders("claude-cli", "codex");

        var result = UnsafeGate.Evaluate(
            config, unsafeMode: true, agentExecutorMode: "claude-cli");

        Assert.True(result.Allow);
        Assert.Empty(result.BlockedProviders);
    }

    // ── Default deny path ────────────────────────────────────────────────────

    [Fact]
    public void ProductionMode_WorkflowReferencesClaudeCli_Blocks()
    {
        var config = ConfigWithProviders("claude-cli");

        var result = UnsafeGate.Evaluate(
            config, unsafeMode: false, agentExecutorMode: null);

        Assert.False(result.Allow);
        Assert.Equal(new[] { "claude-cli" }, result.BlockedProviders);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("claude-cli", result.ErrorMessage);
        Assert.Contains("--unsafe", result.ErrorMessage);
        Assert.Contains("docker-claude-cli", result.ErrorMessage);
    }

    [Fact]
    public void ProductionMode_WorkflowReferencesCodex_Blocks()
    {
        var config = ConfigWithProviders("codex");

        var result = UnsafeGate.Evaluate(
            config, unsafeMode: false, agentExecutorMode: null);

        Assert.False(result.Allow);
        Assert.Equal(new[] { "codex" }, result.BlockedProviders);
        Assert.Contains("docker-codex", result.ErrorMessage!);
    }

    [Fact]
    public void ProductionMode_BothHostProviders_BlockedAlphabetically()
    {
        var config = ConfigWithProviders("codex", "claude-cli");

        var result = UnsafeGate.Evaluate(
            config, unsafeMode: false, agentExecutorMode: null);

        Assert.False(result.Allow);
        // Alphabetised so the operator-facing message is deterministic.
        Assert.Equal(new[] { "claude-cli", "codex" }, result.BlockedProviders);
    }

    [Fact]
    public void ProductionMode_OnlySandboxedProviders_Allows()
    {
        var config = ConfigWithProviders(
            "docker-claude-cli", "docker-codex", "docker-opencode", "docker-claude-qwen");

        var result = UnsafeGate.Evaluate(
            config, unsafeMode: false, agentExecutorMode: null);

        Assert.True(result.Allow);
        Assert.Empty(result.BlockedProviders);
    }

    [Fact]
    public void ProductionMode_NoProviders_Allows()
    {
        var config = ConfigWithProviders();

        var result = UnsafeGate.Evaluate(
            config, unsafeMode: false, agentExecutorMode: null);

        Assert.True(result.Allow);
    }

    // ── Carve-out interaction with case insensitivity ────────────────────────

    [Fact]
    public void WorkflowProviderCasing_IsNormalised()
    {
        // GetAllReferencedProviders returns a case-insensitive set; the gate
        // must not bypass the carve-out check on a casing variation.
        var config = ConfigWithProviders("Claude-CLI");

        var result = UnsafeGate.Evaluate(
            config, unsafeMode: false, agentExecutorMode: null);

        Assert.False(result.Allow);
    }

    // ── Argument validation ──────────────────────────────────────────────────

    [Fact]
    public void NullConfig_Throws()
    {
        Assert.Throws<ArgumentNullException>(() =>
            UnsafeGate.Evaluate(null!, unsafeMode: false, agentExecutorMode: null));
    }

    [Fact]
    public void NullExecutorMode_TreatedAsProductionMode()
    {
        // Defensive: if AgentExecutor isn't set in config, agentExecutorMode is
        // effectively null. Should still gate (not crash, not allow).
        var config = ConfigWithProviders("codex");

        var result = UnsafeGate.Evaluate(config, unsafeMode: false, agentExecutorMode: null);

        Assert.False(result.Allow);
    }

    [Fact]
    public void EmptyExecutorMode_TreatedAsProductionMode()
    {
        var config = ConfigWithProviders("codex");

        var result = UnsafeGate.Evaluate(config, unsafeMode: false, agentExecutorMode: "");

        Assert.False(result.Allow);
    }

    [Fact]
    public void OtherDockerModes_DoNotRedirectClaudeCli()
    {
        // docker-opencode and docker-claude-qwen don't alias claude-cli; only
        // docker-claude-cli does. A workflow referencing claude-cli under
        // docker-claude-qwen mode would fail at resolver time anyway, so the
        // gate should fire and surface the misconfiguration early.
        var config = ConfigWithProviders("claude-cli");

        var resultQwen = UnsafeGate.Evaluate(
            config, unsafeMode: false, agentExecutorMode: "docker-claude-qwen");
        var resultOpenCode = UnsafeGate.Evaluate(
            config, unsafeMode: false, agentExecutorMode: "docker-opencode");
        var resultDockerCodex = UnsafeGate.Evaluate(
            config, unsafeMode: false, agentExecutorMode: "docker-codex");

        Assert.False(resultQwen.Allow);
        Assert.False(resultOpenCode.Allow);
        Assert.False(resultDockerCodex.Allow);
    }
}
