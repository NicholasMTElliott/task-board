using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Tests.Helpers;

namespace TaskBoard.Worker.Tests.Clients;

/// <summary>
/// Contract-level tests for <see cref="DockerOpenCodeAgentExecutor"/>. Unlike
/// Claude/Codex which emit NDJSON, the OpenCode output parser accepts either a
/// raw JSON object at the end of stdout or a ```json-fenced block. The test
/// stdout builders here produce the fenced-block shape to exercise the most
/// common real-world case.
/// </summary>
public class DockerOpenCodeAgentExecutorContractTests : AgentExecutorContractTests
{
    protected override IAgentExecutor CreateExecutor(ProcessRunnerDelegate processRunner)
    {
        var options = TestOptionsMonitor.Create(new DockerOpenCodeAgentOptions
        {
            ImageName = "aiboard-opencode-test:latest",
            TimeoutSeconds = 30,
            // Disable retries so malformed-output tests fail fast instead of
            // exhausting the retry budget against the stubbed runner.
            MaxRetriesOnMalformedOutput = 0,
        });
        // No mount builder — the executor proceeds without workspace mounts,
        // which is fine for contract tests that only exercise post-process branches.
        return new DockerOpenCodeAgentExecutor(
            options,
            Helpers.TestTenant.Instance,
            NullLogger<DockerOpenCodeAgentExecutor>.Instance,
            mountBuilder: null,
            processRunner: processRunner);
    }

    // OpenCode stdout shape: free-form prose ending in a ```json fenced block.
    // The parser finds the fence and extracts the JSON.
    protected override string BuildCompleteStdout(string detail) =>
        "I looked at the task and here is what I did.\n\n```json\n" +
        "{\"outcome\":\"COMPLETE\",\"detail\":\"" + detail + "\"}\n" +
        "```\n";

    protected override string BuildNeedsInfoStdout(string detail) =>
        "I need to ask something first.\n\n```json\n" +
        "{\"outcome\":\"NEEDS_INFO\",\"detail\":\"" + detail + "\"}\n" +
        "```\n";

    protected override string BuildErrorOutcomeStdout(string detail) =>
        "Something went wrong internally.\n\n```json\n" +
        "{\"outcome\":\"ERROR\",\"detail\":\"" + detail + "\"}\n" +
        "```\n";

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
            Model: "qwen3.6-35b-a3b");
    }
}
