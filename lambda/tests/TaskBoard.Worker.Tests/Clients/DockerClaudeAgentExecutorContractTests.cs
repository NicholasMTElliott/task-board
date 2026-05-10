using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

public class DockerClaudeAgentExecutorContractTests : AgentExecutorContractTests
{
    protected override IAgentExecutor CreateExecutor(ProcessRunnerDelegate processRunner)
    {
        var options = Options.Create(new DockerClaudeAgentOptions
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

    /// <summary>
    /// Claude CLI 2.1.x emits its rate-limit signal in stdout NDJSON, NOT stderr.
    /// Production failure shape from KvA project (cards 6/10/12, 2026-05-10): exit
    /// code 1, empty stderr, only init + rejected rate_limit_event in stdout.
    /// Pre-fix this got classified as AGENT_ERROR; this test pins the corrected
    /// classification to RATE_LIMIT.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_NonZeroExit_WithRateLimitEventInStdout_ThrowsRateLimitException()
    {
        var workspace = NewWorkspace();
        try
        {
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

    /// <summary>
    /// Docker exit codes (125/126/127/137) are infrastructure failures, not
    /// rate-limits. Even if the stdout happens to contain a rate_limit_event line
    /// (e.g. CLI partially started before the daemon killed it), the higher-priority
    /// Docker classification must win — otherwise a flaky daemon would masquerade
    /// as a rate-limit and the card-restoration path would loop pointlessly.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_DockerExitCode_WithRateLimitEventInStdout_StillInfrastructure()
    {
        var workspace = NewWorkspace();
        try
        {
            var stdout = """
{"type":"rate_limit_event","rate_limit_info":{"status":"rejected"}}
""";
            // Exit 125 = docker daemon error.
            var executor = CreateExecutor(StubRunner(exitCode: 125, stdout: stdout, stderr: ""));

            await Assert.ThrowsAsync<CliInfrastructureException>(
                () => executor.ExecuteAsync(CreateContext(workspace), CancellationToken.None));
        }
        finally { CleanupWorkspace(workspace); }
    }
}
