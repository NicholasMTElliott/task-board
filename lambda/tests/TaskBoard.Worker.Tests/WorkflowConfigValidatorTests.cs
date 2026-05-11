using System.Text.Json;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Tests;

public class WorkflowConfigValidatorTests
{
    private static WorkflowConfig MakeValidConfig()
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", null, "agent_run",
                    null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-review"),
                        ["ERROR"]    = TransitionTarget.ForColumn("list-error"),
                    },
                    Steps: new List<WorkflowStep>
                    {
                        new("analyze", "ba", TaskPrompt: "Analyze the card."),
                    }),
                ["list-review"] = new WorkflowState(
                    "Review", null, "manual_gate", null,
                    new Dictionary<string, TransitionTarget>()),
                ["list-error"] = new WorkflowState(
                    "Error", null, "holding", null,
                    new Dictionary<string, TransitionTarget>())
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "You are a BA.", new List<string> { "Requirements" })
            });
    }

    [Fact]
    public void ValidConfig_ReturnsNoErrors()
    {
        var errors = WorkflowConfigValidator.Validate(MakeValidConfig());

        Assert.Empty(errors);
    }

    [Fact]
    public void AgentRunState_ReferencesMissingRole_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", null, "agent_run",
                    null, new Dictionary<string, TransitionTarget>(),
                    Steps: new List<WorkflowStep>
                    {
                        new("analyze", "nonexistent_role", TaskPrompt: "Analyze."),
                    })
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("nonexistent_role") && e.Contains("does not exist"));
    }

    [Fact]
    public void AgentRunState_MissingTaskPrompt_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    null, new Dictionary<string, TransitionTarget>())
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("taskPrompt"));
    }

    [Fact]
    public void TransitionTargets_NonExistentState_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.",
                    new Dictionary<string, TransitionTarget> { ["COMPLETE"] = TransitionTarget.ForColumn("list-does-not-exist") })
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("list-does-not-exist") && e.Contains("not mapped"));
    }

    [Fact]
    public void EmptyStatesDict_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>(),
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("States"));
    }

    [Fact]
    public void AgentRunState_TaskPromptFileOnly_PassesValidation()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", null, "agent_run",
                    null, new Dictionary<string, TransitionTarget>(),
                    Steps: new List<WorkflowStep>
                    {
                        new("analyze", "ba", TaskPromptFile: "prompts/states/requirements.md"),
                    })
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e =>
            e.Contains("has no taskPrompt") || e.Contains("has no taskPromptFile"));
    }

    [Fact]
    public void AgentRunState_BothTaskPromptAndFile_PassesValidation()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", null, "agent_run",
                    null, new Dictionary<string, TransitionTarget>(),
                    Steps: new List<WorkflowStep>
                    {
                        new("analyze", "ba",
                            TaskPrompt: "Inline prompt",
                            TaskPromptFile: "prompts/states/requirements.md"),
                    })
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e =>
            e.Contains("has no taskPrompt") || e.Contains("has no taskPromptFile"));
    }

    [Fact]
    public void AgentRunState_NeitherTaskPromptNorFile_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    null, new Dictionary<string, TransitionTarget>())
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("taskPrompt") && e.Contains("taskPromptFile"));
    }

    [Fact]
    public void Role_SystemPromptFileOnly_PassesValidation()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>())
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "", new List<string> { "Requirements" },
                    SystemPromptFile: "prompts/ba.md")
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("systemPrompt"));
    }

    [Fact]
    public void Role_NeitherSystemPromptNorFile_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", null, "agent_run",
                    null, new Dictionary<string, TransitionTarget>(),
                    Steps: new List<WorkflowStep>
                    {
                        new("analyze", "ba", TaskPrompt: "Analyze."),
                    })
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("systemPrompt") && e.Contains("systemPromptFile"));
    }

    // ── children_complete validation ─────────────────────────────────────────

    [Fact]
    public void ChildrenCompleteState_MissingCompleteTransition_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Awaiting Children"] = new("Awaiting Children", null, "children_complete", null,
                    new Dictionary<string, TransitionTarget>
                    {
                        // Missing COMPLETE transition
                        ["ERROR"] = TransitionTarget.ForColumn("Error"),
                    }),
                ["Error"] = new("Error", null, "holding", null, new()),
            },
            Roles: new());

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("children_complete") && e.Contains("COMPLETE"));
    }

    [Fact]
    public void ChildrenCompleteState_WithCompleteTransition_PassesValidation()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Awaiting Children"] = new("Awaiting Children", null, "children_complete", null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("Ready for Test"),
                        ["ERROR"]    = TransitionTarget.ForColumn("Error"),
                    }),
                ["Ready for Test"] = new("Ready for Test", "qa", "agent_run", "Test it.",
                    new Dictionary<string, TransitionTarget>()),
                ["Error"] = new("Error", null, "holding", null, new()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["qa"] = new("model", "prompt", [])
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("children_complete"));
    }

    // ── generationConfig validation ───────────────────────────────────────────

    [Fact]
    public void GenerationConfig_TargetTypeNotInCardTypes_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready for Design"] = new("Ready for Design", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>(),
                    Steps: [new WorkflowStep("gen", "role1", TaskPrompt: "x",
                        GenerationConfig: new GenerationConfig("bug"))]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["role1"] = new("model", "prompt", [])
            },
            CardTypes: new Dictionary<string, CardTypeDefinition>
            {
                ["task"] = new("Task"),   // "bug" not defined
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("bug") && e.Contains("not defined in cardTypes"));
    }

    [Fact]
    public void GenerationConfig_TargetTypeInCardTypes_PassesValidation()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready for Design"] = new("Ready for Design", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>(),
                    Steps: [new WorkflowStep("gen", "role1", TaskPrompt: "x",
                        GenerationConfig: new GenerationConfig("task", "Backlog"))]),
                ["Backlog"] = new("Backlog", null, "manual_entry", null, new()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["role1"] = new("model", "prompt", [])
            },
            CardTypes: new Dictionary<string, CardTypeDefinition>
            {
                ["task"] = new("Task"),
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("not defined in cardTypes"));
    }

    [Fact]
    public void GenerationConfig_UnknownTargetColumn_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready for Design"] = new("Ready for Design", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>(),
                    Steps: [new WorkflowStep("gen", "role1", TaskPrompt: "x",
                        GenerationConfig: new GenerationConfig("task", "NonExistentColumn"))]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["role1"] = new("model", "prompt", [])
            },
            CardTypes: new Dictionary<string, CardTypeDefinition>
            {
                ["task"] = new("Task"),
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("NonExistentColumn") && e.Contains("not a known column"));
    }

    [Fact]
    public void GenerationConfig_WithNullCardTypes_PassesTargetTypeCheck()
    {
        // When CardTypes is null, no type validation can occur — should pass
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready for Design"] = new("Ready for Design", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>(),
                    Steps: [new WorkflowStep("gen", "role1", TaskPrompt: "x",
                        GenerationConfig: new GenerationConfig("task"))]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["role1"] = new("model", "prompt", [])
            }
            // CardTypes is null
        );

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("not defined in cardTypes"));
    }

    [Fact]
    public void MultiStepState_ValidSteps_PassesValidation()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", null, "agent_run",
                    null, new Dictionary<string, TransitionTarget>(),
                    Steps:
                    [
                        new WorkflowStep("design", "ba", TaskPrompt: "Design it."),
                        new WorkflowStep("review", "ba", TaskPrompt: "Review it."),
                    ])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("taskPrompt") || e.Contains("role"));
    }

    [Fact]
    public void MultiStepState_DuplicateStepNames_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", null, "agent_run",
                    null, new Dictionary<string, TransitionTarget>(),
                    Steps:
                    [
                        new WorkflowStep("design", "ba", TaskPrompt: "Design it."),
                        new WorkflowStep("design", "ba", TaskPrompt: "Design again."),
                    ])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("duplicate step name") && e.Contains("design"));
    }

    [Fact]
    public void MultiStepState_StepReferencesMissingRole_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", null, "agent_run",
                    null, new Dictionary<string, TransitionTarget>(),
                    Steps:
                    [
                        new WorkflowStep("design", "nonexistent_role", TaskPrompt: "Design it."),
                    ])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("nonexistent_role") && e.Contains("does not exist"));
    }

    [Fact]
    public void MultiStepState_StepMissingTaskPrompt_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", null, "agent_run",
                    null, new Dictionary<string, TransitionTarget>(),
                    Steps:
                    [
                        new WorkflowStep("design", "ba"),
                    ])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("taskPrompt") && e.Contains("design"));
    }

    // ── Legacy state-level shape rejection (v0.0.25+) ──────────────────────
    // Pre-v0.0.25 a state could declare `role` + `taskPrompt[File]` at the
    // state level and the runtime auto-normalised it into a one-element
    // `steps` array. The auto-conversion is gone; the validator now hard-
    // errors on the legacy shape. These tests pin the new behavior.

    [Fact]
    public void LegacyStateLevelRole_IsRejected()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Design"] = new WorkflowState(
                    "Design", "senior_engineer", "agent_run",
                    null, new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new WorkflowRole(
                    "claude-opus-4-6", "prompt", new List<string> { "Design" }),
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("top-level 'role' field") && e.Contains("v0.0.25"));
    }

    [Fact]
    public void LegacyStateLevelTaskPrompt_IsRejected()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Design"] = new WorkflowState(
                    "Design", null, "agent_run",
                    "Design it.", new Dictionary<string, TransitionTarget>(),
                    Steps: new List<WorkflowStep>
                    {
                        new("create_design", "senior_engineer", TaskPrompt: "x"),
                    }),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new WorkflowRole(
                    "claude-opus-4-6", "prompt", new List<string> { "Design" }),
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("top-level 'taskPrompt' field"));
    }

    [Fact]
    public void LegacyStateLevelTaskPromptFile_IsRejected()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Design"] = new WorkflowState(
                    "Design", null, "agent_run",
                    null, new Dictionary<string, TransitionTarget>(),
                    TaskPromptFile: "prompts/design.md",
                    Steps: new List<WorkflowStep>
                    {
                        new("create_design", "senior_engineer", TaskPrompt: "x"),
                    }),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new WorkflowRole(
                    "claude-opus-4-6", "prompt", new List<string> { "Design" }),
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("top-level 'taskPromptFile' field"));
    }

    [Fact]
    public void AgentRunStateWithoutSteps_IsRejected()
    {
        // Pre-v0.0.25 an agent_run state could omit both `steps` AND
        // `role`/`taskPrompt` and validator just complained about missing
        // legacy fields. Now: every agent_run state needs a `steps` array.
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Design"] = new WorkflowState(
                    "Design", null, "agent_run",
                    null, new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new WorkflowRole(
                    "claude-opus-4-6", "prompt", new List<string> { "Design" }),
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e =>
            e.Contains("agent_run") && e.Contains("no 'steps' array"));
    }

    // ── Role fallback validation (v0.0.25+) ────────────────────────────────
    // Roles can declare an ordered Fallbacks chain that fires on configured
    // exception categories from the primary provider.

    private static WorkflowConfig MakeConfigWithRole(WorkflowRole role)
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Design"] = new WorkflowState(
                    "Design", null, "agent_run",
                    null, new Dictionary<string, TransitionTarget>(),
                    Steps: new List<WorkflowStep>
                    {
                        new("design", "senior_engineer", TaskPrompt: "do it"),
                    }),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = role,
            });
    }

    [Fact]
    public void Role_FallbacksWithKnownProviders_PassesValidation()
    {
        var role = new WorkflowRole(
            "claude-opus-4-6", "prompt", new List<string> { "Design" },
            Provider: "docker-claude-cli",
            Fallbacks: new List<RoleFallback>
            {
                new("docker-codex", Model: "gpt-5.5"),
                new("docker-opencode", Model: "qwen3.6-35b-a3b"),
            });

        var errors = WorkflowConfigValidator.Validate(MakeConfigWithRole(role));

        Assert.DoesNotContain(errors, e => e.Contains("fallback"));
    }

    [Fact]
    public void Role_FallbackWithEmptyProvider_ReportsError()
    {
        var role = new WorkflowRole(
            "claude-opus-4-6", "prompt", new List<string> { "Design" },
            Provider: "docker-claude-cli",
            Fallbacks: new List<RoleFallback> { new("") });

        var errors = WorkflowConfigValidator.Validate(MakeConfigWithRole(role));

        Assert.Contains(errors, e => e.Contains("fallback") && e.Contains("empty provider"));
    }

    [Fact]
    public void Role_FallbackWithUnknownProvider_ReportsError()
    {
        // Operator typo'd 'docker-codexx' → catch at startup, not 5 hours into
        // a polling run when the fallback first fires.
        var role = new WorkflowRole(
            "claude-opus-4-6", "prompt", new List<string> { "Design" },
            Provider: "docker-claude-cli",
            Fallbacks: new List<RoleFallback> { new("docker-codexx") });

        var errors = WorkflowConfigValidator.Validate(MakeConfigWithRole(role));

        Assert.Contains(errors, e =>
            e.Contains("docker-codexx") && e.Contains("unknown provider"));
    }

    [Fact]
    public void Role_FallbackOnContainsAgentError_PassesValidation()
    {
        // AGENT_ERROR here is an exception category: the executor threw before
        // a valid AgentResult existed. It does not apply to in-band
        // outcome=ERROR results, so the validator allows it.
        var role = new WorkflowRole(
            "claude-opus-4-6", "prompt", new List<string> { "Design" },
            Provider: "docker-claude-cli",
            Fallbacks: new List<RoleFallback> { new("docker-codex", "gpt-5.5") },
            FallbackOn: new List<FailureReason> { FailureReason.AGENT_ERROR });

        var errors = WorkflowConfigValidator.Validate(MakeConfigWithRole(role));

        Assert.DoesNotContain(errors, e => e.Contains("AGENT_ERROR"));
    }

    [Fact]
    public void Role_FallbackOnSetWithNoFallbacks_ReportsError()
    {
        // Misconfiguration: set FallbackOn without any fallbacks. The set has
        // no behaviour. Must error so operators catch the missing chain.
        var role = new WorkflowRole(
            "claude-opus-4-6", "prompt", new List<string> { "Design" },
            Provider: "docker-claude-cli",
            FallbackOn: new List<FailureReason> { FailureReason.RATE_LIMIT });

        var errors = WorkflowConfigValidator.Validate(MakeConfigWithRole(role));

        Assert.Contains(errors, e =>
            e.Contains("fallbackOn") && e.Contains("no fallbacks declared"));
    }

    [Fact]
    public void Role_FallbackOnCustomCategories_PassesValidation()
    {
        // Operators can narrow fallback to a subset of exception categories.
        var role = new WorkflowRole(
            "claude-opus-4-6", "prompt", new List<string> { "Design" },
            Provider: "docker-claude-cli",
            Fallbacks: new List<RoleFallback> { new("docker-codex", "gpt-5.5") },
            FallbackOn: new List<FailureReason>
            {
                FailureReason.TIMEOUT,
                FailureReason.AGENT_ERROR,
            });

        var errors = WorkflowConfigValidator.Validate(MakeConfigWithRole(role));

        Assert.DoesNotContain(errors, e => e.Contains("fallback"));
    }

    [Fact]
    public void Role_FallbackProvidersAreEnumeratedByGetAllReferenced()
    {
        // GetAllReferencedProviders must surface fallback providers so
        // --mode validation's image-probe pass tests their Docker images
        // even when the primary doesn't reference them.
        var config = MakeConfigWithRole(new WorkflowRole(
            "claude-opus-4-6", "prompt", new List<string> { "Design" },
            Provider: "docker-claude-cli",
            Fallbacks: new List<RoleFallback>
            {
                new("docker-codex", Model: "gpt-5.5"),
                new("docker-claude-qwen", Model: "qwen3.6-35b-a3b-think"),
            }));

        var providers = config.GetAllReferencedProviders();

        Assert.Contains("docker-claude-cli", providers);
        Assert.Contains("docker-codex", providers);
        Assert.Contains("docker-claude-qwen", providers);
    }

    // ── Gate check validation tests ────────────────────────────────

    [Fact]
    public void GateCheck_RoleExistsAndHasPrompt_PassesValidation()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>(),
                    GateCheck: new GateCheckConfig("gate_checker", TaskPrompt: "Check it.")),
                ["list-review"] = new WorkflowState(
                    "Review", null, "manual_gate", null,
                    new Dictionary<string, TransitionTarget>())
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" }),
                ["gate_checker"] = new WorkflowRole("haiku", "gate prompt", new List<string>())
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("gateCheck"));
    }

    [Fact]
    public void GateCheck_MissingRole_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>(),
                    GateCheck: new GateCheckConfig("nonexistent_gate_role", TaskPrompt: "Check it."))
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("nonexistent_gate_role") && e.Contains("does not exist"));
    }

    [Fact]
    public void GateCheck_MissingTaskPrompt_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>(),
                    GateCheck: new GateCheckConfig("gate_checker"))
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" }),
                ["gate_checker"] = new WorkflowRole("haiku", "gate prompt", new List<string>())
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("gateCheck") && e.Contains("taskPromptFile") && e.Contains("taskPrompt"));
    }

    [Fact]
    public void GateCheck_TaskPromptFileOnly_PassesValidation()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>(),
                    GateCheck: new GateCheckConfig("gate_checker", TaskPromptFile: "prompts/gates/check.md"))
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" }),
                ["gate_checker"] = new WorkflowRole("haiku", "gate prompt", new List<string>())
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("gateCheck"));
    }

    [Fact]
    public void GateCheck_NullGateCheck_NoErrors()
    {
        // State without gate check should not produce gate check errors
        var errors = WorkflowConfigValidator.Validate(MakeValidConfig());

        Assert.DoesNotContain(errors, e => e.Contains("gateCheck"));
    }

    // ── Optional steps validation tests ──────────────────────────────

    [Fact]
    public void OptionalSteps_Valid_PassesValidation()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>(),
                    GateCheck: new GateCheckConfig("gate_checker", TaskPrompt: "Check it."),
                    OptionalSteps:
                    [
                        new OptionalStepDefinition("security_audit", "specialist_reviewer",
                            TaskPrompt: "Perform security audit.")
                    ]),
                ["list-done"] = new WorkflowState("Done", null, "terminal", null,
                    new Dictionary<string, TransitionTarget>())
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("model", "prompt", new List<string> { "Section" }),
                ["gate_checker"] = new WorkflowRole("haiku", "gate prompt", new List<string>()),
                ["specialist_reviewer"] = new WorkflowRole("sonnet", "specialist prompt", new List<string>())
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("optional"));
    }

    [Fact]
    public void OptionalSteps_MissingRole_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>(),
                    GateCheck: new GateCheckConfig("gate_checker", TaskPrompt: "Check it."),
                    OptionalSteps:
                    [
                        new OptionalStepDefinition("security_audit", "nonexistent_role",
                            TaskPrompt: "Check security.")
                    ])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("model", "prompt", new List<string> { "Section" }),
                ["gate_checker"] = new WorkflowRole("haiku", "gate prompt", new List<string>())
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("nonexistent_role") && e.Contains("does not exist"));
    }

    [Fact]
    public void OptionalSteps_MissingPrompt_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>(),
                    GateCheck: new GateCheckConfig("gate_checker", TaskPrompt: "Check it."),
                    OptionalSteps:
                    [
                        new OptionalStepDefinition("security_audit", "specialist_reviewer")
                        // No TaskPrompt or TaskPromptFile
                    ])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("model", "prompt", new List<string> { "Section" }),
                ["gate_checker"] = new WorkflowRole("haiku", "gate prompt", new List<string>()),
                ["specialist_reviewer"] = new WorkflowRole("sonnet", "specialist prompt", new List<string>())
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("security_audit") && e.Contains("taskPrompt"));
    }

    [Fact]
    public void OptionalSteps_DuplicateName_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>(),
                    GateCheck: new GateCheckConfig("gate_checker", TaskPrompt: "Check it."),
                    OptionalSteps:
                    [
                        new OptionalStepDefinition("security_audit", "specialist_reviewer",
                            TaskPrompt: "First."),
                        new OptionalStepDefinition("security_audit", "specialist_reviewer",
                            TaskPrompt: "Duplicate.")
                    ])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("model", "prompt", new List<string> { "Section" }),
                ["gate_checker"] = new WorkflowRole("haiku", "gate prompt", new List<string>()),
                ["specialist_reviewer"] = new WorkflowRole("sonnet", "specialist prompt", new List<string>())
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("duplicate") && e.Contains("security_audit"));
    }

    [Fact]
    public void OptionalSteps_NameCollidesWithMandatoryStep_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    null, new Dictionary<string, TransitionTarget>(),
                    Steps:
                    [
                        new WorkflowStep("implement", "ba", TaskPrompt: "Do work.")
                    ],
                    GateCheck: new GateCheckConfig("gate_checker", TaskPrompt: "Check it."),
                    OptionalSteps:
                    [
                        new OptionalStepDefinition("implement", "specialist_reviewer",
                            TaskPrompt: "Same name as mandatory step.")
                    ])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("model", "prompt", new List<string> { "Section" }),
                ["gate_checker"] = new WorkflowRole("haiku", "gate prompt", new List<string>()),
                ["specialist_reviewer"] = new WorkflowRole("sonnet", "specialist prompt", new List<string>())
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("implement") && e.Contains("collides"));
    }

    [Fact]
    public void OptionalSteps_NoGateCheck_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>(),
                    // No GateCheck
                    OptionalSteps:
                    [
                        new OptionalStepDefinition("security_audit", "specialist_reviewer",
                            TaskPrompt: "Check security.")
                    ])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("model", "prompt", new List<string> { "Section" }),
                ["specialist_reviewer"] = new WorkflowRole("sonnet", "specialist prompt", new List<string>())
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("optionalSteps") && e.Contains("gateCheck"));
    }

    [Fact]
    public void OptionalSteps_Deserialization_FromJson()
    {
        var json = """
        {
            "states": {
                "s1": {
                    "name": "S1",
                    "role": "se",
                    "gateType": "agent_run",
                    "taskPrompt": "Do it.",
                    "transitions": {},
                    "gateCheck": { "role": "gate_checker", "taskPrompt": "Check." },
                    "optionalSteps": [
                        {
                            "name": "security_audit",
                            "role": "specialist_reviewer",
                            "taskPromptFile": "prompts/optional-steps/impl/security_audit.md",
                            "description": "Security review.",
                            "triggers": "Auth changes.",
                            "providerParams": { "effort": "medium" }
                        }
                    ]
                }
            },
            "roles": {
                "se": { "model": "opus", "systemPrompt": "SE.", "sections": ["X"] },
                "gate_checker": { "model": "haiku", "systemPrompt": "Gate.", "sections": [] },
                "specialist_reviewer": { "model": "sonnet", "systemPrompt": "Specialist.", "sections": [] }
            }
        }
        """;

        var config = System.Text.Json.JsonSerializer.Deserialize<WorkflowConfig>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(config);
        var state = config!.States["s1"];
        Assert.NotNull(state.OptionalSteps);
        Assert.Single(state.OptionalSteps!);
        var step = state.OptionalSteps[0];
        Assert.Equal("security_audit", step.Name);
        Assert.Equal("specialist_reviewer", step.Role);
        Assert.Equal("prompts/optional-steps/impl/security_audit.md", step.TaskPromptFile);
        Assert.Equal("Security review.", step.Description);
        Assert.Equal("Auth changes.", step.Triggers);
        Assert.NotNull(step.ProviderParams);
        Assert.Equal("medium", step.ProviderParams!["effort"]);
    }

    [Fact]
    public void OptionalSteps_Deserialization_AbsentKeyIsNull()
    {
        var json = """
        {
            "states": {
                "s1": {
                    "name": "S1",
                    "role": "se",
                    "gateType": "agent_run",
                    "taskPrompt": "Do it.",
                    "transitions": {}
                }
            },
            "roles": {
                "se": { "model": "opus", "systemPrompt": "SE.", "sections": ["X"] }
            }
        }
        """;

        var config = System.Text.Json.JsonSerializer.Deserialize<WorkflowConfig>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Null(config!.States["s1"].OptionalSteps);
    }

    // ── GateCheckConfig deserialization tests ────────────────────────

    [Fact]
    public void GateCheckConfig_Deserialization_FromJson()
    {
        var json = """
        {
            "states": {
                "Ready for Design": {
                    "name": "Ready for Design",
                    "role": "se",
                    "gateType": "agent_run",
                    "taskPrompt": "Design it.",
                    "transitions": {},
                    "gateCheck": {
                        "role": "gate_checker",
                        "taskPromptFile": "prompts/gates/post_design.md",
                        "maxDiffChars": 30000,
                        "maxRetries": 3
                    }
                }
            },
            "roles": {
                "se": { "model": "opus", "systemPrompt": "You are SE.", "sections": ["Design"] },
                "gate_checker": { "model": "haiku", "systemPrompt": "You are gate.", "sections": [] }
            }
        }
        """;

        var config = System.Text.Json.JsonSerializer.Deserialize<WorkflowConfig>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(config);
        var state = config!.States["Ready for Design"];
        Assert.NotNull(state.GateCheck);
        Assert.Equal("gate_checker", state.GateCheck!.Role);
        Assert.Equal("prompts/gates/post_design.md", state.GateCheck.TaskPromptFile);
        Assert.Equal(30_000, state.GateCheck.MaxDiffChars);
        Assert.Equal(3, state.GateCheck.MaxRetries);
    }

    [Fact]
    public void GateCheckConfig_Deserialization_Defaults()
    {
        var json = """
        {
            "states": {
                "s1": {
                    "name": "S1",
                    "role": "se",
                    "gateType": "agent_run",
                    "taskPrompt": "Do it.",
                    "transitions": {},
                    "gateCheck": {
                        "role": "gate_checker",
                        "taskPrompt": "Check it."
                    }
                }
            },
            "roles": {
                "se": { "model": "opus", "systemPrompt": "SE.", "sections": ["X"] },
                "gate_checker": { "model": "haiku", "systemPrompt": "Gate.", "sections": [] }
            }
        }
        """;

        var config = System.Text.Json.JsonSerializer.Deserialize<WorkflowConfig>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        var gc = config!.States["s1"].GateCheck!;
        // Default raised to 51_200 (50 KiB) in Round-8 to match the rerun
        // redesign's rerun.diff.summaryThresholdBytes default. Workflow JSON
        // can still override per-gate via "maxDiffChars".
        Assert.Equal(51_200, gc.MaxDiffChars); // default
        Assert.Equal(2, gc.MaxRetries); // default
        Assert.Null(gc.TaskPromptFile);
        Assert.Equal("Check it.", gc.TaskPrompt);
    }

    [Fact]
    public void GateCheckConfig_Deserialization_NullWhenAbsent()
    {
        var json = """
        {
            "states": {
                "s1": {
                    "name": "S1",
                    "role": "se",
                    "gateType": "agent_run",
                    "taskPrompt": "Do it.",
                    "transitions": {}
                }
            },
            "roles": {
                "se": { "model": "opus", "systemPrompt": "SE.", "sections": ["X"] }
            }
        }
        """;

        var config = System.Text.Json.JsonSerializer.Deserialize<WorkflowConfig>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.Null(config!.States["s1"].GateCheck);
    }

    [Fact]
    public void ProductionConfig_IsValid()
    {
        var repoRoot = FindRepoRoot();
        var configPath = Path.Combine(repoRoot, "workflow.github.json");
        var json = File.ReadAllText(configPath);
        var config = JsonSerializer.Deserialize<WorkflowConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        })!;

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.True(errors.Count == 0,
            $"Production workflow.github.json has validation errors:\n{string.Join("\n", errors)}");
    }

    [Fact]
    public void RoleWithEmptySections_PassesValidation()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>())
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string>())
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("empty Sections"));
    }

    // ── Action validation tests ────────────────────────────────────

    [Fact]
    public void Transition_UnknownActionType_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = new TransitionTarget(
                            [new TransitionAction("unknownActionType", "value")])
                    })
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("unknownActionType"));
    }

    [Fact]
    public void Transition_MoveToColumn_MissingValue_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = new TransitionTarget(
                            [new TransitionAction(ActionTypes.MoveToColumn)])
                    })
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains(ActionTypes.MoveToColumn) && e.Contains("requires a value"));
    }

    [Fact]
    public void Transition_SetField_MissingFieldName_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = new TransitionTarget(
                            [new TransitionAction(ActionTypes.SetField, "value")])
                    })
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains(ActionTypes.SetField) && e.Contains("field name"));
    }

    [Fact]
    public void Transition_MultipleActions_ValidConfig_PassesValidation()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-done"),
                    }),
                ["list-done"] = new WorkflowState("Done", null, "terminal", null,
                    new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        // Force direct construction with valid multi-action transition
        var validConfig = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = new TransitionTarget(
                        [
                            new TransitionAction(ActionTypes.MoveToColumn, "list-done"),
                            new TransitionAction(ActionTypes.AddLabel, "done"),
                        ])
                    }),
                ["list-done"] = new WorkflowState("Done", null, "terminal", null,
                    new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(validConfig);

        Assert.DoesNotContain(errors, e => e.Contains("COMPLETE"));
    }

    // ── UpdateParentSum validation ────────────────────────────────

    [Fact]
    public void Transition_UpdateParentSum_ValidConfig_PassesValidation()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = new TransitionTarget(
                        [
                            new TransitionAction(ActionTypes.MoveToColumn, "list-done"),
                            new TransitionAction(ActionTypes.SetField, "4", Field: "Estimate"),
                            new TransitionAction(ActionTypes.UpdateParentSum, Field: "Estimate"),
                        ])
                    }),
                ["list-done"] = new WorkflowState("Done", null, "terminal", null,
                    new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("updateParentSum"));
    }

    [Fact]
    public void Transition_UpdateParentSum_MissingField_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = new TransitionTarget(
                        [
                            new TransitionAction(ActionTypes.MoveToColumn, "list-done"),
                            new TransitionAction(ActionTypes.UpdateParentSum), // missing field
                        ])
                    }),
                ["list-done"] = new WorkflowState("Done", null, "terminal", null,
                    new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("updateParentSum") && e.Contains("field name"));
    }

    // ── CompleteParentIfReady validation ────────────────────────────

    [Fact]
    public void Transition_CompleteParentIfReady_ValidConfig_PassesValidation()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-approved"] = new WorkflowState(
                    "Approved", null, "system_merge",
                    null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = new TransitionTarget(
                        [
                            new TransitionAction(ActionTypes.MoveToColumn, "list-done"),
                            new TransitionAction(ActionTypes.CompleteParentIfReady),
                        ])
                    }),
                ["list-done"] = new WorkflowState("Done", null, "terminal", null,
                    new Dictionary<string, TransitionTarget>()),
            },
            Roles: new());

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("completeParentIfReady"));
    }

    // ── Filter validation tests ────────────────────────────────────

    [Fact]
    public void Filter_LabelExists_ValidConfig_PassesValidation()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>(),
                    Filters: [new CardFilter(FilterTypes.Label, FilterOperators.Exists, "ai-ready")])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("filter"));
    }

    [Fact]
    public void Filter_UnknownFilterType_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>(),
                    Filters: [new CardFilter("unknownType", FilterOperators.Exists, "value")])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("unknownType"));
    }

    [Fact]
    public void Filter_LabelWithInvalidOperator_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>(),
                    Filters: [new CardFilter(FilterTypes.Label, FilterOperators.Equals, "ai-ready")])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains(FilterOperators.Equals) && e.Contains("label"));
    }

    [Fact]
    public void Filter_LabelMissingValue_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>(),
                    Filters: [new CardFilter(FilterTypes.Label, FilterOperators.Exists)])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("label filter requires a value"));
    }

    [Fact]
    public void Filter_AssigneeIsEmpty_ValidConfig_PassesValidation()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>(),
                    Filters: [new CardFilter(FilterTypes.Assignee, FilterOperators.IsEmpty)])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("filter"));
    }

    [Fact]
    public void Filter_FieldTypeWithNoFieldName_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>(),
                    Filters: [new CardFilter(FilterTypes.Field, FilterOperators.Equals, "P0")])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("field filter requires a 'field'"));
    }

    [Fact]
    public void Filter_EqualsOperatorMissingValue_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>(),
                    Filters: [new CardFilter(FilterTypes.Assignee, FilterOperators.Equals)])
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains(FilterOperators.Equals) && e.Contains("requires a value"));
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException(
            $"Could not find repo root (no .git directory) starting from {AppContext.BaseDirectory}");
    }
}

// ── AllowedChildren cross-check tests ──────────────────────────────────────────

public class WorkflowConfigValidatorAllowedChildrenTests
{
    private static WorkflowConfig MakeConfigWithCardTypes(
        Dictionary<string, CardTypeDefinition> cardTypes)
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Backlog"] = new("Backlog", null, "manual_entry", null, new())
            },
            Roles: new(),
            CardTypes: cardTypes);
    }

    [Fact]
    public void AllowedChildren_AllValuesAreValidTypes_PassesValidation()
    {
        var config = MakeConfigWithCardTypes(new Dictionary<string, CardTypeDefinition>
        {
            ["story"] = new("User Story", "type", ["task"]),
            ["task"]  = new("Task",       "type", []),
        });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("allowedChildren"));
    }

    [Fact]
    public void AllowedChildren_ContainsUnknownType_ReportsError()
    {
        var config = MakeConfigWithCardTypes(new Dictionary<string, CardTypeDefinition>
        {
            ["story"] = new("User Story", "type", ["task", "typo_type"]),
            ["task"]  = new("Task",       "type", []),
        });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("typo_type") && e.Contains("allowedChildren") && e.Contains("story"));
    }

    [Fact]
    public void AllowedChildren_EmptyList_PassesValidation()
    {
        var config = MakeConfigWithCardTypes(new Dictionary<string, CardTypeDefinition>
        {
            ["task"] = new("Task", "type", []),
        });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("allowedChildren"));
    }

    [Fact]
    public void AllowedChildren_NullList_PassesValidation()
    {
        var config = MakeConfigWithCardTypes(new Dictionary<string, CardTypeDefinition>
        {
            ["task"] = new("Task"),  // AllowedChildren defaults to null
        });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("allowedChildren"));
    }

    [Fact]
    public void AllowedChildren_NullCardTypes_PassesValidation()
    {
        // When CardTypes is null entirely, no allowedChildren validation occurs
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Backlog"] = new("Backlog", null, "manual_entry", null, new())
            },
            Roles: new()
            // CardTypes is null
        );

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("allowedChildren"));
    }

    [Fact]
    public void AllowedChildren_MultipleTypesWithErrors_ReportsAllErrors()
    {
        var config = MakeConfigWithCardTypes(new Dictionary<string, CardTypeDefinition>
        {
            ["story"]   = new("User Story", "type", ["task", "invalid_a"]),
            ["feature"] = new("Feature",    "type", ["story", "invalid_b"]),
            ["task"]    = new("Task",       "type", []),
        });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("invalid_a") && e.Contains("story"));
        Assert.Contains(errors, e => e.Contains("invalid_b") && e.Contains("feature"));
    }

    // ── New v-mode checks: enum/numeric/uniqueness ─────────────────────────

    private static WorkflowConfig MakeValidConfig()
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", null, "agent_run",
                    null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-review"),
                        ["ERROR"]    = TransitionTarget.ForColumn("list-error"),
                    },
                    Steps: new List<WorkflowStep>
                    {
                        new("analyze", "ba", TaskPrompt: "Analyze the card."),
                    }),
                ["list-review"] = new WorkflowState(
                    "Review", null, "manual_gate", null,
                    new Dictionary<string, TransitionTarget>()),
                ["list-error"] = new WorkflowState(
                    "Error", null, "holding", null,
                    new Dictionary<string, TransitionTarget>())
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "You are a BA.", new List<string> { "Requirements" })
            });
    }

    [Fact]
    public void UnknownGateType_ReportsError()
    {
        var cfg = MakeValidConfig();
        cfg.States["list-req"] = cfg.States["list-req"] with { GateType = "bogus_gate" };

        var errors = WorkflowConfigValidator.Validate(cfg);

        Assert.Contains(errors, e => e.Contains("gateType") && e.Contains("bogus_gate"));
    }

    [Fact]
    public void UnknownGitBehavior_ReportsError()
    {
        var cfg = MakeValidConfig();
        cfg.States["list-req"] = cfg.States["list-req"] with { GitBehavior = "squash_merge" };

        var errors = WorkflowConfigValidator.Validate(cfg);

        Assert.Contains(errors, e => e.Contains("gitBehavior") && e.Contains("squash_merge"));
    }

    [Theory]
    [InlineData("discard")]
    [InlineData("commit_only")]
    [InlineData("commit_and_push")]
    public void KnownGitBehaviorValues_DoNotError(string value)
    {
        var cfg = MakeValidConfig();
        cfg.States["list-req"] = cfg.States["list-req"] with { GitBehavior = value };

        var errors = WorkflowConfigValidator.Validate(cfg);

        Assert.DoesNotContain(errors, e => e.Contains("gitBehavior"));
    }

    [Fact]
    public void RoleWithEmptyModel_ReportsError()
    {
        var cfg = MakeValidConfig();
        cfg.Roles["ba"] = cfg.Roles["ba"] with { Model = "" };

        var errors = WorkflowConfigValidator.Validate(cfg);

        Assert.Contains(errors, e => e.Contains("Role 'ba'") && e.Contains("no model"));
    }

    [Fact]
    public void EstimationScale_NonMonotonic_ReportsError()
    {
        var cfg = MakeValidConfig() with
        {
            Estimation = new EstimationConfig("1", 1, "Estimate", [1, 2, 2, 4]),
        };

        var errors = WorkflowConfigValidator.Validate(cfg);

        Assert.Contains(errors, e => e.Contains("monotonic"));
    }

    [Fact]
    public void EstimationScale_CalibrationSizeNotInScale_ReportsError()
    {
        var cfg = MakeValidConfig() with
        {
            Estimation = new EstimationConfig("1", 3, "Estimate", [1, 2, 4, 8]),
        };

        var errors = WorkflowConfigValidator.Validate(cfg);

        Assert.Contains(errors, e => e.Contains("calibrationSize") && e.Contains("scale"));
    }

    [Fact]
    public void EstimationScale_Valid_DoesNotError()
    {
        var cfg = MakeValidConfig() with
        {
            Estimation = new EstimationConfig("1", 1, "Estimate", [1, 2, 4, 8]),
        };

        var errors = WorkflowConfigValidator.Validate(cfg);

        Assert.DoesNotContain(errors, e => e.Contains("scale") || e.Contains("calibrationSize"));
    }

    [Fact]
    public void CardType_NoDiscriminator_ReportsError()
    {
        // No labelPrefix AND no workflow-level cardTypeField → error
        var cfg = MakeValidConfig() with
        {
            CardTypes = new Dictionary<string, CardTypeDefinition>
            {
                ["story"] = new("Story", "", []),
            },
            CardTypeField = null,
        };

        var errors = WorkflowConfigValidator.Validate(cfg);

        Assert.Contains(errors, e => e.Contains("discriminator") && e.Contains("story"));
    }

    [Fact]
    public void CardType_EmptyLabelPrefixWithCardTypeField_IsAccepted()
    {
        // Empty labelPrefix is OK when workflow has cardTypeField (field-based discrimination)
        var cfg = MakeValidConfig() with
        {
            CardTypes = new Dictionary<string, CardTypeDefinition>
            {
                ["story"] = new("Story", LabelPrefix: null, AllowedChildren: []),
            },
            CardTypeField = "Type",
        };

        var errors = WorkflowConfigValidator.Validate(cfg);

        Assert.DoesNotContain(errors, e => e.Contains("discriminator"));
    }

    private static WorkflowConfig MakeConfigWithGenerationStep(GenerationConfig genCfg)
    {
        var step = new WorkflowStep("gen", "ba", TaskPrompt: "prompt", GenerationConfig: genCfg);
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["gen-state"] = new WorkflowState(
                    "Generate", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("gen-state"),
                    },
                    Steps: [step]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "sys", ["Requirements"]),
            },
            CardTypes: new Dictionary<string, CardTypeDefinition>
            {
                ["task"] = new("Task", "type", []),
            });
    }

    [Fact]
    public void GenerationConfig_SetFields_EmptyKey_ReportsError()
    {
        var cfg = MakeConfigWithGenerationStep(new GenerationConfig(
            TargetType: "task",
            TargetColumn: "gen-state",
            SetFields: new Dictionary<string, string> { [""] = "x" }));

        var errors = WorkflowConfigValidator.Validate(cfg);

        Assert.Contains(errors, e => e.Contains("setFields") && e.Contains("empty field name"));
    }

    [Fact]
    public void GenerationConfig_SetFields_EmptyValue_ReportsError()
    {
        var cfg = MakeConfigWithGenerationStep(new GenerationConfig(
            TargetType: "task",
            TargetColumn: "gen-state",
            SetFields: new Dictionary<string, string> { ["Activity"] = "" }));

        var errors = WorkflowConfigValidator.Validate(cfg);

        Assert.Contains(errors, e => e.Contains("setFields") && e.Contains("Activity") && e.Contains("empty value"));
    }

    [Fact]
    public void GenerationConfig_SetFields_Valid_NoErrors()
    {
        var cfg = MakeConfigWithGenerationStep(new GenerationConfig(
            TargetType: "task",
            TargetColumn: "gen-state",
            SetFields: new Dictionary<string, string>
            {
                ["Type"] = "Task",
                ["Activity"] = "Design",
            }));

        var errors = WorkflowConfigValidator.Validate(cfg);

        Assert.DoesNotContain(errors, e => e.Contains("setFields"));
    }

    [Fact]
    public void OptionalStepName_DuplicatedAcrossStates_ReportsError()
    {
        var gate = new GateCheckConfig("ba", TaskPrompt: "check");
        var optional = new OptionalStepDefinition(
            Name: "shared_review", Role: "ba", TaskPrompt: "p",
            Description: "d", Triggers: "t");

        var cfg = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["A"] = new("A", "ba", "agent_run", "p",
                    new Dictionary<string, TransitionTarget>(),
                    GateCheck: gate,
                    OptionalSteps: [optional]),
                ["B"] = new("B", "ba", "agent_run", "p",
                    new Dictionary<string, TransitionTarget>(),
                    GateCheck: gate,
                    OptionalSteps: [optional]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new("gpt-4.1", "sys", ["Requirements"]),
            });

        var errors = WorkflowConfigValidator.Validate(cfg);

        Assert.Contains(errors, e =>
            e.Contains("shared_review") && e.Contains("unique across the whole config"));
    }

    // ── Audit: Codex sandbox defaults ─────────────────────────────────────────

    private static WorkflowConfig MakeCodexConfig(
        Dictionary<string, string>? stateProviderParams = null,
        Dictionary<string, string>? optionalStepProviderParams = null,
        bool includeOptionalStep = false)
    {
        var steps = new List<WorkflowStep>
        {
            new("implement", "codex_implementer", "Do the thing."),
        };

        var optSteps = includeOptionalStep
            ? new List<OptionalStepDefinition>
            {
                new("security_review", "codex_implementer", "Review security.",
                    Description: "Security review",
                    Triggers: "security-sensitive",
                    ProviderParams: optionalStepProviderParams),
            }
            : null;

        GateCheckConfig? gate = includeOptionalStep
            ? new GateCheckConfig("codex_implementer", TaskPrompt: "gate")
            : null;

        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["impl"] = new("Implementing", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("impl"),
                        ["ERROR"]    = TransitionTarget.ForColumn("impl"),
                    },
                    ProviderParams: stateProviderParams,
                    Steps: steps,
                    GateCheck: gate,
                    OptionalSteps: optSteps),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["codex_implementer"] = new("gpt-4.1", "You are a dev.",
                    new List<string> { "Implementation" }, Provider: "codex"),
            });
    }

    [Fact]
    public void Audit_CodexStepWithoutSandbox_ProducesWarning()
    {
        var cfg = MakeCodexConfig();

        var warnings = WorkflowConfigValidator.Audit(cfg);

        Assert.Contains(warnings, w =>
            w.Contains("Codex role 'codex_implementer'")
            && w.Contains("no sandbox policy"));
    }

    [Theory]
    [InlineData("sandbox", "read-only")]
    [InlineData("yolo", "true")]
    [InlineData("fullAuto", "true")]
    public void Audit_CodexStepWithExplicitSandboxKey_NoWarning(string key, string value)
    {
        var cfg = MakeCodexConfig(stateProviderParams: new Dictionary<string, string>
        {
            [key] = value,
        });

        var warnings = WorkflowConfigValidator.Audit(cfg);

        Assert.DoesNotContain(warnings, w => w.Contains("codex_implementer"));
    }

    [Fact]
    public void Audit_OptionalCodexStep_InheritsStateSandboxChoice()
    {
        var cfg = MakeCodexConfig(
            stateProviderParams: new Dictionary<string, string> { ["sandbox"] = "read-only" },
            includeOptionalStep: true);

        var warnings = WorkflowConfigValidator.Audit(cfg);

        Assert.DoesNotContain(warnings, w => w.Contains("security_review"));
    }

    [Fact]
    public void Audit_OptionalCodexStep_CanOverrideWithItsOwnParams()
    {
        var cfg = MakeCodexConfig(
            stateProviderParams: null,
            optionalStepProviderParams: new Dictionary<string, string> { ["sandbox"] = "workspace-write" },
            includeOptionalStep: true);

        var warnings = WorkflowConfigValidator.Audit(cfg);

        Assert.DoesNotContain(warnings, w => w.Contains("security_review"));
    }

    [Fact]
    public void Audit_OptionalCodexStep_NoSandboxAtAll_ProducesWarning()
    {
        var cfg = MakeCodexConfig(includeOptionalStep: true);

        var warnings = WorkflowConfigValidator.Audit(cfg);

        Assert.Contains(warnings, w =>
            w.Contains("optional step 'security_review'")
            && w.Contains("no sandbox policy"));
    }

    [Fact]
    public void Audit_NonAgentRunState_IsIgnored()
    {
        // Roles on manual-gate / holding / terminal states never execute,
        // so no sandbox warning applies.
        var cfg = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["tested"] = new("Tested", null, "manual_gate", null,
                    new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new("gpt-4.1", "sys", ["x"], Provider: "codex"),
            });

        var warnings = WorkflowConfigValidator.Audit(cfg);

        Assert.Empty(warnings);
    }

    [Fact]
    public void Audit_NonCodexRole_NoWarning()
    {
        var cfg = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["impl"] = new("Implementing", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("impl"),
                        ["ERROR"]    = TransitionTarget.ForColumn("impl"),
                    },
                    Steps: [new("step1", "claude_role", "prompt")]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                // Default Provider = "claude-cli"
                ["claude_role"] = new("claude-sonnet-4-6", "sys", ["x"]),
            });

        var warnings = WorkflowConfigValidator.Audit(cfg);

        Assert.Empty(warnings);
    }

    // ── Candidate-group validation (Part 2) ──────────────────────────────────

    private static WorkflowConfig MakeCandidateConfig(
        WorkflowStep step, string gitBehavior = "commit_and_push") =>
        new(
            States: new Dictionary<string, WorkflowState>
            {
                ["impl"] = new(
                    Name: "Implementing",
                    Role: null,
                    GateType: "agent_run",
                    TaskPrompt: null,
                    Transitions: new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("impl"),
                        ["ERROR"]    = TransitionTarget.ForColumn("impl"),
                    },
                    GitBehavior: gitBehavior,
                    Steps: [step]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["implementer"] = new("claude-sonnet-4-6", "sys", ["Implementation"]),
                ["evaluator"] = new("claude-opus-4-6", "sys", []),
            });

    [Fact]
    public void SingleCandidate_WithoutEvaluator_IsAllowed()
    {
        // A single-candidate slot may omit the evaluator — the runtime surfaces
        // the candidate's outcome directly. This is the natural shape for the
        // last fallback slot in a chain ("if all the expensive providers
        // failed, just run a local Qwen and use whatever it produces").
        var step = new WorkflowStep(
            Name: "implement",
            Role: "implementer",
            TaskPromptFile: "prompts/states/impl.md",
            Candidates: [new CandidateOverride("docker-claude-cli")]);

        var errors = WorkflowConfigValidator.Validate(MakeCandidateConfig(step));

        Assert.DoesNotContain(errors, e =>
            e.Contains("candidates but no evaluator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void MultipleCandidates_WithoutEvaluator_ReportsError()
    {
        // A multi-candidate slot REQUIRES an evaluator to pick the winner.
        // Without one there's no way to choose between the parallel results.
        var step = new WorkflowStep(
            Name: "implement",
            Role: "implementer",
            TaskPromptFile: "prompts/states/impl.md",
            Candidates:
            [
                new CandidateOverride("docker-claude-cli"),
                new CandidateOverride("docker-opencode"),
            ]);

        var errors = WorkflowConfigValidator.Validate(MakeCandidateConfig(step));

        Assert.Contains(errors, e =>
            e.Contains("candidates but no evaluator", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluator_WithoutCandidates_ReportsError()
    {
        var step = new WorkflowStep(
            Name: "implement",
            Role: "implementer",
            TaskPromptFile: "prompts/states/impl.md",
            Evaluator: new EvaluatorConfig("evaluator", TaskPrompt: "evaluate"));

        var errors = WorkflowConfigValidator.Validate(MakeCandidateConfig(step));

        Assert.Contains(errors, e =>
            e.Contains("evaluator but no candidates", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Candidates_DuplicateProviders_AllowedForSameProviderModelABTesting()
    {
        // Same-provider, different-model A/B testing is a legitimate use case
        // (e.g. Opus vs Sonnet on Claude). Variance-measurement runs (same exact
        // agent twice) are also legitimate. The validator no longer rejects
        // duplicate providers — uniqueness is the user's call.
        var step = new WorkflowStep(
            Name: "implement",
            Role: "implementer",
            TaskPromptFile: "prompts/states/impl.md",
            Candidates:
            [
                new CandidateOverride("docker-claude-cli", Model: "claude-opus-4-6"),
                new CandidateOverride("docker-claude-cli", Model: "claude-sonnet-4-6"),
            ],
            Evaluator: new EvaluatorConfig("evaluator", TaskPrompt: "evaluate"));

        var errors = WorkflowConfigValidator.Validate(MakeCandidateConfig(step));

        Assert.DoesNotContain(errors, e =>
            e.Contains("duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Candidates_ManyAllowed_NoArtificialCap()
    {
        // The previous cap-of-4 was self-imposed conservatism. Eval prompt size
        // and wall time are user-managed concerns, not validator concerns.
        var step = new WorkflowStep(
            Name: "implement",
            Role: "implementer",
            TaskPromptFile: "prompts/states/impl.md",
            Candidates:
            [
                new CandidateOverride("p1"), new CandidateOverride("p2"),
                new CandidateOverride("p3"), new CandidateOverride("p4"),
                new CandidateOverride("p5"), new CandidateOverride("p6"),
                new CandidateOverride("p7"), new CandidateOverride("p8"),
            ],
            Evaluator: new EvaluatorConfig("evaluator", TaskPrompt: "evaluate"));

        var errors = WorkflowConfigValidator.Validate(MakeCandidateConfig(step));

        Assert.DoesNotContain(errors, e =>
            e.Contains("cap", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Candidates_OnDiscardState_AllowedViaFileBasedPromotion()
    {
        // Discard-mode states (design, tasking) are now supported — CandidateExecutor
        // promotes the winner by copying .aiboard/{tasks,updates}/ contents to the
        // canonical worktree instead of git-resetting.
        var step = new WorkflowStep(
            Name: "create_design",
            Role: "senior_engineer",
            TaskPromptFile: "prompts/states/design.md",
            Candidates: [new CandidateOverride("docker-claude-cli"), new CandidateOverride("docker-opencode")],
            Evaluator: new EvaluatorConfig("evaluator", TaskPrompt: "evaluate"));

        var errors = WorkflowConfigValidator.Validate(MakeCandidateConfig(step, gitBehavior: "discard"));

        Assert.DoesNotContain(errors, e =>
            e.Contains("gitBehavior is 'discard'", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluator_MissingRoleInRoles_ReportsError()
    {
        var step = new WorkflowStep(
            Name: "implement",
            Role: "implementer",
            TaskPromptFile: "prompts/states/impl.md",
            Candidates: [new CandidateOverride("docker-claude-cli"), new CandidateOverride("docker-opencode")],
            Evaluator: new EvaluatorConfig("ghost_judge", TaskPrompt: "evaluate"));

        var errors = WorkflowConfigValidator.Validate(MakeCandidateConfig(step));

        Assert.Contains(errors, e =>
            e.Contains("ghost_judge", StringComparison.OrdinalIgnoreCase)
            && e.Contains("does not exist", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Evaluator_MissingTaskPrompt_ReportsError()
    {
        var step = new WorkflowStep(
            Name: "implement",
            Role: "implementer",
            TaskPromptFile: "prompts/states/impl.md",
            Candidates: [new CandidateOverride("docker-claude-cli"), new CandidateOverride("docker-opencode")],
            Evaluator: new EvaluatorConfig("evaluator"));

        var errors = WorkflowConfigValidator.Validate(MakeCandidateConfig(step));

        Assert.Contains(errors, e =>
            e.Contains("evaluator has neither", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Candidates_ValidConfig_ReturnsNoErrors()
    {
        var step = new WorkflowStep(
            Name: "implement",
            Role: "implementer",
            TaskPromptFile: "prompts/states/impl.md",
            Candidates:
            [
                new CandidateOverride("docker-claude-cli"),
                new CandidateOverride("docker-opencode", Model: "qwen3.6-35b-a3b"),
            ],
            Evaluator: new EvaluatorConfig(
                "evaluator",
                TaskPromptFile: "prompts/evaluator/code_review_candidates.md"));

        var errors = WorkflowConfigValidator.Validate(MakeCandidateConfig(step));

        Assert.Empty(errors);
    }

    // Removed in v0.0.25: the legacy state-level shape (`state.Role` + `state.TaskPrompt`
    // without a Steps array) is now a hard validator error, so the audit branch
    // that warned on it is unreachable and was deleted alongside this test.

    // ── Audit: cross-provider candidate model leak ────────────────────────────

    [Fact]
    public void Audit_CrossProviderCandidate_NoModelOverride_ProducesWarning()
    {
        // Role's provider is claude-cli with a Claude model name; candidate
        // overrides provider to codex but doesn't override model. The role's
        // Claude model must NOT leak into Codex CLI's --model arg — the runtime
        // suppresses it, and the audit warns the operator to pin a model
        // explicitly so the candidate isn't running on whatever the executor's
        // own default happens to be.
        var cfg = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["impl"] = new("Implementing", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("impl"),
                        ["ERROR"]    = TransitionTarget.ForColumn("impl"),
                    },
                    Steps:
                    [
                        new WorkflowStep(
                            Name: "implement",
                            Role: "implementer",
                            TaskPrompt: "do it",
                            Candidates:
                            [
                                new CandidateOverride("codex"),  // no model override
                                new CandidateOverride("docker-claude-cli"),
                            ],
                            Evaluator: new EvaluatorConfig("evaluator", TaskPrompt: "judge")),
                    ]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["implementer"] = new("claude-opus-4-6", "sys", [], Provider: "claude-cli"),
                ["evaluator"]   = new("claude-opus-4-6", "sys", [], Provider: "claude-cli"),
            });

        var warnings = WorkflowConfigValidator.Audit(cfg);

        Assert.Contains(warnings, w =>
            w.Contains("step 'implement' candidate #0")
            && w.Contains("provider 'codex'")
            && w.Contains("Set 'model' on the candidate"));
    }

    [Fact]
    public void Audit_CrossProviderCandidate_WithModelOverride_NoWarning()
    {
        // Same shape as above but candidate pins its own model — the operator
        // has made an intentional choice; no warning.
        var cfg = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["impl"] = new("Implementing", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("impl"),
                        ["ERROR"]    = TransitionTarget.ForColumn("impl"),
                    },
                    Steps:
                    [
                        new WorkflowStep(
                            Name: "implement",
                            Role: "implementer",
                            TaskPrompt: "do it",
                            Candidates:
                            [
                                new CandidateOverride("codex", Model: "gpt-5.4"),
                            ],
                            Evaluator: new EvaluatorConfig("evaluator", TaskPrompt: "judge")),
                    ]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["implementer"] = new("claude-opus-4-6", "sys", [], Provider: "claude-cli"),
                ["evaluator"]   = new("claude-opus-4-6", "sys", [], Provider: "claude-cli"),
            });

        var warnings = WorkflowConfigValidator.Audit(cfg);

        Assert.DoesNotContain(warnings, w => w.Contains("candidate #0"));
    }

    [Fact]
    public void Audit_SameProviderCandidate_InheritsRoleModel_NoWarning()
    {
        // Candidate's provider matches role's — the role's model is the right
        // default, no warning.
        var cfg = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["impl"] = new("Implementing", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("impl"),
                        ["ERROR"]    = TransitionTarget.ForColumn("impl"),
                    },
                    Steps:
                    [
                        new WorkflowStep(
                            Name: "implement",
                            Role: "implementer",
                            TaskPrompt: "do it",
                            Candidates:
                            [
                                new CandidateOverride("claude-cli"),
                            ],
                            Evaluator: new EvaluatorConfig("evaluator", TaskPrompt: "judge")),
                    ]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["implementer"] = new("claude-opus-4-6", "sys", [], Provider: "claude-cli"),
                ["evaluator"]   = new("claude-opus-4-6", "sys", [], Provider: "claude-cli"),
            });

        var warnings = WorkflowConfigValidator.Audit(cfg);

        Assert.DoesNotContain(warnings, w => w.Contains("candidate #0"));
    }

    // ── Rerun config validation ─────────────────────────────────────────────

    private static WorkflowConfig MakeConfigWithRerun(RerunConfig? rerun)
    {
        var c = MakeValidConfig();
        return c with { Rerun = rerun };
    }

    [Fact]
    public void RerunConfig_NullDefaultBranch_NoError()
    {
        // null is the documented "auto-detect via origin/HEAD" sentinel.
        var cfg = MakeConfigWithRerun(new RerunConfig(DefaultBranch: null));
        var errors = WorkflowConfigValidator.Validate(cfg);
        Assert.DoesNotContain(errors, e => e.Contains("rerun.defaultBranch"));
    }

    [Fact]
    public void RerunConfig_EmptyDefaultBranch_ReportsError()
    {
        // An empty/whitespace string is a misconfiguration — the operator
        // probably meant to pass null.
        var cfg = MakeConfigWithRerun(new RerunConfig(DefaultBranch: "   "));
        var errors = WorkflowConfigValidator.Validate(cfg);
        Assert.Contains(errors, e => e.Contains("rerun.defaultBranch"));
    }

    [Fact]
    public void RerunConfig_NonPositiveSummaryThresholdBytes_ReportsError()
    {
        var cfg = MakeConfigWithRerun(new RerunConfig(
            Diff: new RerunDiffConfig(SummaryThresholdBytes: 0)));
        var errors = WorkflowConfigValidator.Validate(cfg);
        Assert.Contains(errors, e => e.Contains("summaryThresholdBytes"));
    }

    [Fact]
    public void RerunConfig_NegativeSummaryThresholdBytes_ReportsError()
    {
        var cfg = MakeConfigWithRerun(new RerunConfig(
            Diff: new RerunDiffConfig(SummaryThresholdBytes: -1)));
        var errors = WorkflowConfigValidator.Validate(cfg);
        Assert.Contains(errors, e => e.Contains("summaryThresholdBytes"));
    }

    [Fact]
    public void RerunConfig_PositiveSummaryThresholdBytes_NoError()
    {
        var cfg = MakeConfigWithRerun(new RerunConfig(
            Diff: new RerunDiffConfig(SummaryThresholdBytes: 51200)));
        var errors = WorkflowConfigValidator.Validate(cfg);
        Assert.DoesNotContain(errors, e => e.Contains("summaryThresholdBytes"));
    }

    [Fact]
    public void RerunConfig_InvalidRetentionPolicyValue_ReportsError()
    {
        // Typo: "appended" instead of "append" — silently falls through at
        // runtime to CommentRouter.DefaultFor, masking the operator's intent.
        // Promote to a hard error at validation.
        var cfg = MakeConfigWithRerun(new RerunConfig(
            Comments: new RerunCommentsConfig(
                RetentionPolicy: new Dictionary<string, string>
                {
                    ["step"] = "appended",
                })));
        var errors = WorkflowConfigValidator.Validate(cfg);
        Assert.Contains(errors, e => e.Contains("rerun.comments.retentionPolicy") && e.Contains("appended"));
    }

    [Fact]
    public void RerunConfig_EmptyRetentionPolicyValue_ReportsError()
    {
        var cfg = MakeConfigWithRerun(new RerunConfig(
            Comments: new RerunCommentsConfig(
                RetentionPolicy: new Dictionary<string, string>
                {
                    ["step"] = "",
                })));
        var errors = WorkflowConfigValidator.Validate(cfg);
        Assert.Contains(errors, e => e.Contains("rerun.comments.retentionPolicy"));
    }

    [Theory]
    [InlineData("append")]
    [InlineData("delete_and_repost")]
    [InlineData("upsert")]
    public void RerunConfig_RecognisedRetentionPolicyValue_NoError(string policy)
    {
        var cfg = MakeConfigWithRerun(new RerunConfig(
            Comments: new RerunCommentsConfig(
                RetentionPolicy: new Dictionary<string, string>
                {
                    ["step"] = policy,
                })));
        var errors = WorkflowConfigValidator.Validate(cfg);
        Assert.DoesNotContain(errors, e => e.Contains("rerun.comments.retentionPolicy"));
    }

    [Fact]
    public void RerunConfig_RetentionPolicyValue_CaseInsensitive()
    {
        // Operators may write "Append" or "APPEND". Trim + ToLowerInvariant
        // before matching keeps the validator forgiving — same as
        // CommentRouter.ParsePolicy.
        var cfg = MakeConfigWithRerun(new RerunConfig(
            Comments: new RerunCommentsConfig(
                RetentionPolicy: new Dictionary<string, string>
                {
                    ["step"] = "  Append ",
                })));
        var errors = WorkflowConfigValidator.Validate(cfg);
        Assert.DoesNotContain(errors, e => e.Contains("rerun.comments.retentionPolicy"));
    }

    [Fact]
    public void RerunConfig_UnknownKindKey_AuditWarns_NotAnError()
    {
        // Unknown kind key (typo or future kind) should not be a hard error —
        // the runtime never consults it, so it's harmless. But it's worth a
        // soft warning so operator typos don't silently fall through.
        var cfg = MakeConfigWithRerun(new RerunConfig(
            Comments: new RerunCommentsConfig(
                RetentionPolicy: new Dictionary<string, string>
                {
                    ["typo_kind"] = "append",
                })));

        var errors = WorkflowConfigValidator.Validate(cfg);
        Assert.DoesNotContain(errors, e => e.Contains("typo_kind"));

        var warnings = WorkflowConfigValidator.Audit(cfg);
        Assert.Contains(warnings, w => w.Contains("typo_kind"));
    }

    [Fact]
    public void RerunConfig_KnownKindKey_NoAuditWarning()
    {
        var cfg = MakeConfigWithRerun(new RerunConfig(
            Comments: new RerunCommentsConfig(
                RetentionPolicy: new Dictionary<string, string>
                {
                    ["step"] = "append",
                    ["dependency_blocked"] = "delete_and_repost",
                    ["created_ticket_dedupe"] = "upsert",
                })));
        var warnings = WorkflowConfigValidator.Audit(cfg);
        Assert.DoesNotContain(warnings, w => w.Contains("rerun.comments.retentionPolicy"));
    }
}
