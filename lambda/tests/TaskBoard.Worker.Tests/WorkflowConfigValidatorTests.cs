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
                    new Dictionary<string, string>
                    {
                        ["COMPLETE"] = "list-review",
                        ["ERROR"] = "list-error"
                    }),
                ["list-review"] = new WorkflowState(
                    "Review", null, "manual_gate", null,
                    new Dictionary<string, string>()),
                ["list-error"] = new WorkflowState(
                    "Error", null, "holding", null,
                    new Dictionary<string, string>())
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
                    "Analyze.", new Dictionary<string, string>())
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
                    null, new Dictionary<string, string>())
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
                    new Dictionary<string, string> { ["COMPLETE"] = "list-does-not-exist" })
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
                    null, new Dictionary<string, string>(),
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
                    "Inline prompt", new Dictionary<string, string>(),
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
                    null, new Dictionary<string, string>())
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
                    "Analyze.", new Dictionary<string, string>())
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
                    "Analyze.", new Dictionary<string, string>())
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "", new List<string> { "Requirements" })
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("systemPrompt") && e.Contains("systemPromptFile"));
    }

    [Fact]
    public void MultiStepState_ValidSteps_PassesValidation()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", null, "agent_run",
                    null, new Dictionary<string, string>(),
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
                    null, new Dictionary<string, string>(),
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
                    null, new Dictionary<string, string>(),
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
                    null, new Dictionary<string, string>(),
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
            "Design it.", new Dictionary<string, string>());

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
            null, new Dictionary<string, string>(), Steps: steps);

        var normalised = WorkflowState.Normalise(raw);

        Assert.Same(raw, normalised);
    }

    [Fact]
    public void Normalise_NonAgentState_ReturnsUnchanged()
    {
        var raw = new WorkflowState("Review", null, "manual_gate",
            null, new Dictionary<string, string>());

        var normalised = WorkflowState.Normalise(raw);

        Assert.Same(raw, normalised);
    }

    [Fact]
    public void RoleWithEmptySections_ReportsError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, string>())
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "prompt", new List<string>())
            });

        var errors = WorkflowConfigValidator.Validate(config);

        Assert.Contains(errors, e => e.Contains("empty Sections"));
    }
}
