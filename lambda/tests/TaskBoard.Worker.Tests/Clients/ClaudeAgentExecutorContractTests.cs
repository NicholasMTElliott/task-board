using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

public class ClaudeAgentExecutorContractTests : AgentExecutorContractTests
{
    protected override IAgentExecutor CreateExecutor(ProcessRunnerDelegate processRunner)
    {
        var options = Options.Create(new ClaudeCliLlmOptions
        {
            ExecutablePath = "claude",
            TimeoutSeconds = 30,
            MaxBudgetUsd = 1.00m,
        });
        return new ClaudeAgentExecutor(
            options, NullLogger<ClaudeAgentExecutor>.Instance, processRunner);
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

    /// <summary>
    /// Claude CLI 2.1.x emits its rate-limit signal in stdout NDJSON, NOT stderr.
    /// Pre-fix this slipped through <c>IsRateLimited(stderr)</c>, the executor
    /// threw <see cref="InvalidOperationException"/>, AgentRunner classified it
    /// as AGENT_ERROR, and the card moved to Problems instead of being held for
    /// a backoff retry. This test pins the corrected behavior.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NonZeroExit_WithRateLimitEventInStdout_ThrowsRateLimitException()
    {
        var workspace = NewWorkspace();
        try
        {
            // Real production shape: only init + rejected rate_limit_event in stdout,
            // empty stderr, exit 1.
            var stdout = """
{"type":"system","subtype":"init","cwd":"/workspace","session_id":"abc"}
{"type":"rate_limit_event","rate_limit_info":{"status":"rejected","resetsAt":1778438400,"rateLimitType":"five_hour","overageStatus":"rejected","overageDisabledReason":"out_of_credits","isUsingOverage":false}}
""";
            var executor = CreateExecutor(StubRunner(exitCode: 1, stdout: stdout, stderr: ""));

            var ex = await Assert.ThrowsAsync<RateLimitException>(
                () => executor.ExecuteAsync(CreateContext(workspace), CancellationToken.None));
            Assert.Equal(RateLimitSource.AgentCli, ex.Source);
        }
        finally { CleanupWorkspace(workspace); }
    }
}
