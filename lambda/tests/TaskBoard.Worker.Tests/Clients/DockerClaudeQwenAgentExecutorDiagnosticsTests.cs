using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

/// <summary>
/// Targeted diagnostics for <see cref="DockerClaudeQwenAgentExecutor"/>:
/// model selection, rate-limit detection on the Qwen-target path, and
/// command-line shape (the actual end-to-end agent contract path is exercised
/// by the shared <see cref="AgentExecutorContractTests"/> base, applied via a
/// dedicated subclass elsewhere).
/// </summary>
public class DockerClaudeQwenAgentExecutorDiagnosticsTests
{
    private static DockerClaudeQwenAgentExecutor CreateExecutor(
        ProcessRunnerDelegate runner,
        DockerClaudeQwenAgentOptions? opts = null) =>
        new(
            Options.Create(opts ?? new DockerClaudeQwenAgentOptions
            {
                ImageName = "aiboard-cq-test:latest",
                TimeoutSeconds = 30,
            }),
            Helpers.TestTenant.Instance,
            NullLogger<DockerClaudeQwenAgentExecutor>.Instance,
            mountBuilder: null,
            processRunner: runner);

    private static (string Workspace, string SystemPromptFile) NewWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "cq-diag-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var promptFile = Path.Combine(dir, "system.md");
        File.WriteAllText(promptFile, "# System\nTest.");
        return (dir, promptFile);
    }

    private static void Cleanup(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    private static AgentExecutionContext NewContext(
        string ws, string promptFile, string? model = null) =>
        new(
            TargetCardId: "42",
            TargetCardTitle: "Test",
            WorkspacePath: ws,
            TaskPrompt: "do the thing",
            SystemPromptFilePath: promptFile,
            Model: model ?? "qwen3.6-35b-a3b");

    /// <summary>
    /// Stream-json result envelope with a structured_output block — the same
    /// shape the real Claude CLI emits, so the parser path is unchanged
    /// regardless of whether the backend is real Anthropic or the local proxy.
    /// </summary>
    private static string StreamJsonResult(string outcome, string detail) =>
        $"{{\"type\":\"result\",\"structured_output\":{{\"outcome\":\"{outcome}\",\"detail\":\"{detail}\"}}}}";

    [Fact]
    public async Task ExecuteAsync_ContextModelOverride_PassedAsCliModelFlag()
    {
        // The role's Model wins over the configured default and surfaces on the
        // Claude CLI command line. This is what makes per-role no-think /
        // -think selection work.
        var (ws, promptFile) = NewWorkspace();
        try
        {
            string[]? capturedArgs = null;
            ProcessRunnerDelegate capturingRunner = (exe, args, wd, t, ct, stdin, rm, n) =>
            {
                capturedArgs = args;
                return Task.FromResult((0, StreamJsonResult("COMPLETE", "ok"), ""));
            };

            var executor = CreateExecutor(capturingRunner,
                new DockerClaudeQwenAgentOptions
                {
                    ImageName = "aiboard-cq-test:latest",
                    ModelName = "qwen3.6-35b-a3b",
                });

            var ctx = NewContext(ws, promptFile, model: "qwen3.6-35b-a3b-think");
            var result = await executor.ExecuteAsync(ctx, CancellationToken.None);

            Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
            Assert.NotNull(capturedArgs);

            // --model must appear and be paired with the override, not the option default.
            var modelIdx = Array.IndexOf(capturedArgs!, "--model");
            Assert.True(modelIdx >= 0 && modelIdx + 1 < capturedArgs.Length,
                "Expected --model flag in CLI args");
            Assert.Equal("qwen3.6-35b-a3b-think", capturedArgs[modelIdx + 1]);
        }
        finally { Cleanup(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_EmptyContextModel_FallsBackToOptionDefault()
    {
        var (ws, promptFile) = NewWorkspace();
        try
        {
            string[]? capturedArgs = null;
            ProcessRunnerDelegate capturingRunner = (exe, args, wd, t, ct, stdin, rm, n) =>
            {
                capturedArgs = args;
                return Task.FromResult((0, StreamJsonResult("COMPLETE", "ok"), ""));
            };

            var executor = CreateExecutor(capturingRunner,
                new DockerClaudeQwenAgentOptions
                {
                    ImageName = "aiboard-cq-test:latest",
                    ModelName = "qwen3.6-35b-a3b",
                });

            var ctx = NewContext(ws, promptFile, model: "");
            await executor.ExecuteAsync(ctx, CancellationToken.None);

            var modelIdx = Array.IndexOf(capturedArgs!, "--model");
            Assert.Equal("qwen3.6-35b-a3b", capturedArgs![modelIdx + 1]);
        }
        finally { Cleanup(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_RateLimitedStderrNonZeroExit_ThrowsRateLimitException()
    {
        var (ws, promptFile) = NewWorkspace();
        try
        {
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n)
                => Task.FromResult((1, "", "Error: rate limit exceeded"));

            var executor = CreateExecutor(runner);

            var ex = await Assert.ThrowsAsync<RateLimitException>(() =>
                executor.ExecuteAsync(NewContext(ws, promptFile), CancellationToken.None));
            Assert.Equal(RateLimitSource.AgentCli, ex.Source);
        }
        finally { Cleanup(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_RateLimitedStderrEmptyStdoutZeroExit_ThrowsRateLimitException()
    {
        var (ws, promptFile) = NewWorkspace();
        try
        {
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n)
                => Task.FromResult((0, "", "rate limit exceeded — try later"));

            var executor = CreateExecutor(runner);

            var ex = await Assert.ThrowsAsync<RateLimitException>(() =>
                executor.ExecuteAsync(NewContext(ws, promptFile), CancellationToken.None));
            Assert.Equal(RateLimitSource.AgentCli, ex.Source);
        }
        finally { Cleanup(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_DockerExitCode127_ThrowsCliInfrastructureException()
    {
        // Exit 127 from docker = command not found in image (claude binary
        // missing). Must classify as infrastructure so AgentRunner records
        // FailureReason=INFRASTRUCTURE rather than AGENT_ERROR.
        var (ws, promptFile) = NewWorkspace();
        try
        {
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n)
                => Task.FromResult((127, "", "docker: claude: command not found"));

            var executor = CreateExecutor(runner);

            await Assert.ThrowsAsync<CliInfrastructureException>(() =>
                executor.ExecuteAsync(NewContext(ws, promptFile), CancellationToken.None));
        }
        finally { Cleanup(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_StreamJsonResult_ParsedAsStructuredOutput()
    {
        var (ws, promptFile) = NewWorkspace();
        try
        {
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n)
                => Task.FromResult((0,
                    StreamJsonResult("NEEDS_INFO", "need more"), ""));

            var executor = CreateExecutor(runner);

            var result = await executor.ExecuteAsync(
                NewContext(ws, promptFile), CancellationToken.None);

            Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
            Assert.Equal("need more", result.Detail);
        }
        finally { Cleanup(ws); }
    }
}

/// <summary>
/// Targets <see cref="CliFailureHintDetector.ClaudeQwenSignatures"/> directly.
/// Picks one example per category to make sure the signatures don't silently
/// regress as we tune them.
/// </summary>
public class ClaudeQwenSignatureTests
{
    [Theory]
    [InlineData("Error: network llm-net not found", "Network")]
    [InlineData("could not resolve host llama-server", "Network")]
    [InlineData("connection refused on port 8080", "Network")]
    [InlineData("upstream server returned model_not_found", "Model")]
    [InlineData("context length 200000 exceeds limit", "Model")]
    [InlineData("invalid_api_key for tenant", "Auth")]
    [InlineData("Please set ANTHROPIC_API_KEY", "Auth")]
    [InlineData("HTTP/1.1 404 path missing", "Config")]
    [InlineData("hasCompletedOnboarding required", "Config")]
    public void Detect_KnownSignatures_ReturnsExpectedCategory(string stderr, string expectedCategory)
    {
        var hint = CliFailureHintDetector.Detect(
            stderr, CliFailureHintDetector.ClaudeQwenSignatures);
        Assert.NotNull(hint);
        Assert.Equal(expectedCategory, hint.Category);
    }

    [Fact]
    public void Detect_UnrelatedStderr_ReturnsNull()
    {
        var hint = CliFailureHintDetector.Detect(
            "completely unrelated message about the weather",
            CliFailureHintDetector.ClaudeQwenSignatures);
        Assert.Null(hint);
    }
}
