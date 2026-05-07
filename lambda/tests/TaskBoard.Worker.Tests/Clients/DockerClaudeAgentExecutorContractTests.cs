using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Tests.Helpers;

namespace TaskBoard.Worker.Tests.Clients;

public class DockerClaudeAgentExecutorContractTests : AgentExecutorContractTests
{
    protected override IAgentExecutor CreateExecutor(ProcessRunnerDelegate processRunner)
    {
        var options = TestOptionsMonitor.Create(new DockerClaudeAgentOptions
        {
            ImageName = "aiboard-test:latest",
            TimeoutSeconds = 30,
            MaxBudgetUsd = 1.00m,
        });
        // No mount builder → the executor proceeds without workspace mounts,
        // which is fine for the contract tests that only exercise the post-process branches.
        return new DockerClaudeAgentExecutor(
            options,
            Helpers.TestTenant.Instance,
            NullLogger<DockerClaudeAgentExecutor>.Instance,
            mountBuilder: null,
            processRunner: processRunner);
    }

    protected override string BuildCompleteStdout(string detail) =>
        """{"type":"assistant","message":{"content":[{"type":"text","text":"Working."}]}}""" + "\n" +
        "{\"type\":\"result\",\"structured_output\":{\"outcome\":\"COMPLETE\",\"detail\":\"" + detail + "\"}}";

    protected override string BuildNeedsInfoStdout(string detail) =>
        "{\"type\":\"result\",\"structured_output\":{\"outcome\":\"NEEDS_INFO\",\"detail\":\"" + detail + "\"}}";

    protected override string BuildErrorOutcomeStdout(string detail) =>
        "{\"type\":\"result\",\"structured_output\":{\"outcome\":\"ERROR\",\"detail\":\"" + detail + "\"}}";

    protected override AgentExecutionContext CreateContext(string workspacePath)
    {
        var promptFile = Path.Combine(workspacePath, "system.md");
        File.WriteAllText(promptFile, "# System Prompt\nTest.");
        return new AgentExecutionContext(
            TargetCardId: "42",
            TargetCardTitle: "Test",
            WorkspacePath: workspacePath,
            TaskPrompt: "do the thing",
            SystemPromptFilePath: promptFile,
            Model: "claude-sonnet-4-6");
    }
}
