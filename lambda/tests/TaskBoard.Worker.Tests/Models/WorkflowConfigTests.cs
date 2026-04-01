using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Tests.Models;

public class WorkflowConfigTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static WorkflowConfig MakeConfig(
        Dictionary<string, WorkflowRole>? roles = null,
        List<WorkflowStep>? steps = null,
        GateCheckConfig? gateCheck = null,
        List<OptionalStepDefinition>? optionalSteps = null)
    {
        var stateRoles = roles ?? new Dictionary<string, WorkflowRole>
        {
            ["claude_role"] = new("model", "prompt", new List<string>(), Provider: "claude-cli"),
            ["codex_role"]  = new("model", "prompt", new List<string>(), Provider: "codex"),
            ["gate_role"]   = new("model", "prompt", new List<string>(), Provider: "claude-cli"),
        };

        var state = new WorkflowState(
            "Test State", null, "agent_run", null,
            new Dictionary<string, TransitionTarget>(),
            Steps: steps,
            GateCheck: gateCheck,
            OptionalSteps: optionalSteps);

        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState> { ["test"] = state },
            Roles: stateRoles);
    }

    private static WorkflowState StateOf(WorkflowConfig config) => config.States["test"];

    // ── GetRequiredProviders tests ────────────────────────────────────────────

    [Fact]
    public void GetRequiredProviders_StepsOnly_ReturnsStepProviders()
    {
        var config = MakeConfig(steps:
        [
            new WorkflowStep("step1", "claude_role"),
        ]);

        var providers = config.GetRequiredProviders(StateOf(config));

        Assert.Contains("claude-cli", providers);
        Assert.Single(providers);
    }

    [Fact]
    public void GetRequiredProviders_WithGateCheck_IncludesGateProvider()
    {
        var config = MakeConfig(
            steps: [new WorkflowStep("step1", "claude_role")],
            gateCheck: new GateCheckConfig("gate_role"));

        var providers = config.GetRequiredProviders(StateOf(config));

        Assert.Contains("claude-cli", providers);
        // gate_role also uses "claude-cli" in default roles, so count = 1 (deduped)
        Assert.Single(providers);
    }

    [Fact]
    public void GetRequiredProviders_GateCheckDifferentProvider_IncludesBoth()
    {
        var config = MakeConfig(
            roles: new Dictionary<string, WorkflowRole>
            {
                ["claude_role"] = new("model", "prompt", new List<string>(), Provider: "claude-cli"),
                ["codex_gate"]  = new("model", "prompt", new List<string>(), Provider: "codex"),
            },
            steps: [new WorkflowStep("step1", "claude_role")],
            gateCheck: new GateCheckConfig("codex_gate"));

        var providers = config.GetRequiredProviders(StateOf(config));

        Assert.Contains("claude-cli", providers);
        Assert.Contains("codex", providers);
        Assert.Equal(2, providers.Count);
    }

    [Fact]
    public void GetRequiredProviders_WithOptionalSteps_IncludesOptionalProviders()
    {
        var config = MakeConfig(
            steps: [new WorkflowStep("step1", "claude_role")],
            optionalSteps:
            [
                new OptionalStepDefinition("security", "codex_role", Description: "Security review"),
            ]);

        var providers = config.GetRequiredProviders(StateOf(config));

        Assert.Contains("claude-cli", providers);
        Assert.Contains("codex", providers);
    }

    [Fact]
    public void GetRequiredProviders_MixedProviders_ReturnsDistinctSet()
    {
        var config = MakeConfig(
            roles: new Dictionary<string, WorkflowRole>
            {
                ["claude_role"] = new("model", "prompt", new List<string>(), Provider: "claude-cli"),
                ["codex_role"]  = new("model", "prompt", new List<string>(), Provider: "codex"),
            },
            steps:
            [
                new WorkflowStep("step1", "claude_role"),
                new WorkflowStep("step2", "codex_role"),
                new WorkflowStep("step3", "claude_role"), // duplicate provider
            ]);

        var providers = config.GetRequiredProviders(StateOf(config));

        Assert.Contains("claude-cli", providers);
        Assert.Contains("codex", providers);
        Assert.Equal(2, providers.Count); // deduped
    }

    [Fact]
    public void GetRequiredProviders_AllSameProvider_ReturnsSingleElement()
    {
        var config = MakeConfig(
            roles: new Dictionary<string, WorkflowRole>
            {
                ["r1"] = new("model", "prompt", new List<string>(), Provider: "claude-cli"),
                ["r2"] = new("model", "prompt", new List<string>(), Provider: "claude-cli"),
                ["r3"] = new("model", "prompt", new List<string>(), Provider: "claude-cli"),
            },
            steps:
            [
                new WorkflowStep("step1", "r1"),
                new WorkflowStep("step2", "r2"),
            ],
            gateCheck: new GateCheckConfig("r3"),
            optionalSteps:
            [
                new OptionalStepDefinition("opt", "r1", Description: "Optional"),
            ]);

        var providers = config.GetRequiredProviders(StateOf(config));

        Assert.Single(providers);
        Assert.Contains("claude-cli", providers);
    }

    [Fact]
    public void GetRequiredProviders_NoStepsOrGate_ReturnsEmpty()
    {
        var state = new WorkflowState(
            "Merge", null, "system_merge", null,
            new Dictionary<string, TransitionTarget>());

        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState> { ["merge"] = state },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["some_role"] = new("model", "prompt", new List<string>(), Provider: "claude-cli"),
            });

        var providers = config.GetRequiredProviders(state);

        Assert.Empty(providers);
    }

    [Fact]
    public void GetRequiredProviders_UnknownRoleInStep_SkipsGracefully()
    {
        var config = MakeConfig(
            roles: new Dictionary<string, WorkflowRole>
            {
                ["known_role"] = new("model", "prompt", new List<string>(), Provider: "claude-cli"),
            },
            steps:
            [
                new WorkflowStep("step1", "known_role"),
                new WorkflowStep("step2", "unknown_role"), // not in Roles dict
            ]);

        // Should not throw; unknown role is silently skipped
        var providers = config.GetRequiredProviders(StateOf(config));

        Assert.Contains("claude-cli", providers);
        Assert.Single(providers);
    }

    [Fact]
    public void GetRequiredProviders_CaseInsensitiveDeduplication()
    {
        // Two roles using "Claude-CLI" and "claude-cli" (different case) → should be one entry
        var config = MakeConfig(
            roles: new Dictionary<string, WorkflowRole>
            {
                ["r1"] = new("model", "prompt", new List<string>(), Provider: "Claude-CLI"),
                ["r2"] = new("model", "prompt", new List<string>(), Provider: "claude-cli"),
            },
            steps:
            [
                new WorkflowStep("step1", "r1"),
                new WorkflowStep("step2", "r2"),
            ]);

        var providers = config.GetRequiredProviders(StateOf(config));

        Assert.Single(providers);
    }
}
