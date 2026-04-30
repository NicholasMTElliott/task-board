using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

/// <summary>
/// Contract-level tests for <see cref="DockerCodexAgentExecutor"/>. Same NDJSON
/// shape as the host Codex CLI (the parser is shared via
/// <see cref="CodexOutputParser"/>) — only the process invocation differs.
/// </summary>
public class DockerCodexAgentExecutorContractTests : AgentExecutorContractTests
{
    protected override IAgentExecutor CreateExecutor(ProcessRunnerDelegate processRunner)
    {
        var options = Options.Create(new DockerCodexAgentOptions
        {
            ImageName = "aiboard-codex-test:latest",
            TimeoutSeconds = 30,
        });
        return new DockerCodexAgentExecutor(
            options,
            Helpers.TestTenant.Instance,
            NullLogger<DockerCodexAgentExecutor>.Instance,
            mountBuilder: null,
            processRunner: processRunner);
    }

    protected override string BuildCompleteStdout(string detail) =>
        "{\"type\":\"turn.completed\",\"structured_output\":{\"outcome\":\"COMPLETE\",\"detail\":\"" + detail + "\"}}";

    protected override string BuildNeedsInfoStdout(string detail) =>
        "{\"type\":\"turn.completed\",\"structured_output\":{\"outcome\":\"NEEDS_INFO\",\"detail\":\"" + detail + "\"}}";

    protected override string BuildErrorOutcomeStdout(string detail) =>
        "{\"type\":\"turn.completed\",\"structured_output\":{\"outcome\":\"ERROR\",\"detail\":\"" + detail + "\"}}";

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
            Model: "gpt-5.4-mini");
    }
}
