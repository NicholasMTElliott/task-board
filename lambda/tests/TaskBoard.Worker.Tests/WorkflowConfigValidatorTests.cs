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
