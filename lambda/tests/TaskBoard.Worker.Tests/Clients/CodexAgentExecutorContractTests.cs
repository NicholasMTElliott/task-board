using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Tests.Helpers;

namespace TaskBoard.Worker.Tests.Clients;

public class CodexAgentExecutorContractTests : AgentExecutorContractTests
{
    protected override IAgentExecutor CreateExecutor(ProcessRunnerDelegate processRunner)
    {
        var options = TestOptionsMonitor.Create(new CodexCliLlmOptions
        {
            ExecutablePath = "codex",
            TimeoutSeconds = 30,
            FullAuto = true,
        });
        return new CodexAgentExecutor(
            options, NullLogger<CodexAgentExecutor>.Instance, processRunner);
    }

    // Codex wraps structured_output inside a turn.completed event.
    // The parser accepts structured_output on any NDJSON line.
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
            Model: "codex-mini-latest");
    }
}
