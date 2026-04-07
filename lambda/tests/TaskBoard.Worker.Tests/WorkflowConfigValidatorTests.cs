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
                    "Requirements", "ba", "agent_run",
                    "Analyze the card.",
                    new Dictionary<string, TransitionTarget>
                    {
                        ["COMPLETE"] = TransitionTarget.ForColumn("list-review"),
                        ["ERROR"]    = TransitionTarget.ForColumn("list-error"),
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
                    "Requirements", "nonexistent_role", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>())
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

        Assert.Contains(errors, e => e.Contains("list-does-not-exist") && e.Contains("does not exist"));
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
                    "Requirements", "ba", "agent_run",
                    null, new Dictionary<string, TransitionTarget>(),
                    TaskPromptFile: "prompts/states/requirements.md")
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("taskPrompt"));
    }

    [Fact]
    public void AgentRunState_BothTaskPromptAndFile_PassesValidation()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Inline prompt", new Dictionary<string, TransitionTarget>(),
                    TaskPromptFile: "prompts/states/requirements.md")
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.DoesNotContain(errors, e => e.Contains("taskPrompt"));
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
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, TransitionTarget>())
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

        Assert.Contains(errors, e => e.Contains("NonExistentColumn") && e.Contains("not a known state"));
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

    [Fact]
    public void Normalise_LegacyState_CreatesStepsArray()
    {
        var raw = new WorkflowState("Design", "senior_engineer", "agent_run",
            "Design it.", new Dictionary<string, TransitionTarget>());

        var normalised = WorkflowState.Normalise(raw);

        Assert.NotNull(normalised.Steps);
        Assert.Single(normalised.Steps);
        Assert.Equal("senior_engineer", normalised.Steps[0].Name);
        Assert.Equal("senior_engineer", normalised.Steps[0].Role);
        Assert.Equal("Design it.", normalised.Steps[0].TaskPrompt);
    }

    [Fact]
    public void Normalise_StateWithSteps_ReturnsUnchanged()
    {
        var steps = new List<WorkflowStep>
        {
            new("step1", "ba", TaskPrompt: "Do it."),
        };
        var raw = new WorkflowState("Design", null, "agent_run",
            null, new Dictionary<string, TransitionTarget>(), Steps: steps);

        var normalised = WorkflowState.Normalise(raw);

        Assert.Same(raw, normalised);
    }

    [Fact]
    public void Normalise_NonAgentState_ReturnsUnchanged()
    {
        var raw = new WorkflowState("Review", null, "manual_gate",
            null, new Dictionary<string, TransitionTarget>());

        var normalised = WorkflowState.Normalise(raw);

        Assert.Same(raw, normalised);
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
        Assert.Equal(50_000, gc.MaxDiffChars); // default
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
}
