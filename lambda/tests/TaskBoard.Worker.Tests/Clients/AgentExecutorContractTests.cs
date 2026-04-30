using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

/// <summary>
/// Contract tests that every <see cref="IAgentExecutor"/> must satisfy.
/// Each concrete subclass wires the fake <see cref="ProcessRunnerDelegate"/>
/// into a real executor and supplies provider-specific stdout samples.
/// </summary>
/// <remarks>
/// These tests exercise the <see cref="IAgentExecutor.ExecuteAsync"/> path by
/// replaying prerecorded (exitCode, stdout, stderr) via the delegate seam. No
/// real CLI subprocess is launched.
/// </remarks>
public abstract class AgentExecutorContractTests
{
    /// <summary>
    /// Creates an executor wired to the provided delegate. Must return a freshly
    /// constructed instance each call.
    /// </summary>
    protected abstract IAgentExecutor CreateExecutor(ProcessRunnerDelegate processRunner);

    /// <summary>
    /// Provider-specific NDJSON stdout that parses to a COMPLETE outcome with
    /// the given detail text.
    /// </summary>
    protected abstract string BuildCompleteStdout(string detail);

    /// <summary>
    /// Provider-specific NDJSON stdout that parses to a NEEDS_INFO outcome.
    /// </summary>
    protected abstract string BuildNeedsInfoStdout(string detail);

    /// <summary>
    /// Provider-specific NDJSON stdout that parses to an agent-reported ERROR outcome.
    /// Distinct from a non-zero exit code: the subprocess exits 0 but the structured
    /// result declares <c>outcome: "ERROR"</c>.
    /// </summary>
    protected abstract string BuildErrorOutcomeStdout(string detail);

    /// <summary>
    /// Constructs an execution context suitable for this executor (system prompt path,
    /// model, etc.). The workspace must be a real directory for <see cref="TaskBoard.Worker.Processing.TaskFileManager"/>
    /// to resolve task file paths; the harness creates a tmp dir per test.
    /// </summary>
    protected abstract AgentExecutionContext CreateContext(string workspacePath);

    private static ProcessRunnerDelegate StubRunner(
        int exitCode, string stdout, string stderr)
    {
        return (exe, args, wd, t, ct, stdin, remove, name, _)
            => Task.FromResult((exitCode, stdout, stderr));
    }

    private string NewWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "executor-contract-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void CleanupWorkspace(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }

    // ── Scenarios ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ValidStdout_ReturnsCompleteOutcome()
    {
        var workspace = NewWorkspace();
        try
        {
            var stdout = BuildCompleteStdout("All done.");
            var executor = CreateExecutor(StubRunner(exitCode: 0, stdout: stdout, stderr: ""));

            var result = await executor.ExecuteAsync(CreateContext(workspace), CancellationToken.None);

            Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
            Assert.Equal("All done.", result.Detail);
        }
        finally { CleanupWorkspace(workspace); }
    }

    [Fact]
    public async Task ExecuteAsync_ValidStdout_ReturnsNeedsInfoOutcome()
    {
        var workspace = NewWorkspace();
        try
        {
            var stdout = BuildNeedsInfoStdout("Need clarification.");
            var executor = CreateExecutor(StubRunner(exitCode: 0, stdout: stdout, stderr: ""));

            var result = await executor.ExecuteAsync(CreateContext(workspace), CancellationToken.None);

            Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
            Assert.Equal("Need clarification.", result.Detail);
        }
        finally { CleanupWorkspace(workspace); }
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExit_WithRateLimitStderr_ThrowsRateLimitException()
    {
        var workspace = NewWorkspace();
        try
        {
            var executor = CreateExecutor(StubRunner(
                exitCode: 1, stdout: "", stderr: "Error: rate limit exceeded"));

            var ex = await Assert.ThrowsAsync<RateLimitException>(
                () => executor.ExecuteAsync(CreateContext(workspace), CancellationToken.None));

            Assert.Equal(RateLimitSource.AgentCli, ex.Source);
        }
        finally { CleanupWorkspace(workspace); }
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExit_WithoutRateLimit_ThrowsInvalidOperation()
    {
        var workspace = NewWorkspace();
        try
        {
            var executor = CreateExecutor(StubRunner(
                exitCode: 1, stdout: "", stderr: "Some other error"));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => executor.ExecuteAsync(CreateContext(workspace), CancellationToken.None));

            Assert.Contains("exited with code 1", ex.Message);
            // Generic-exit errors must NOT be classified as infrastructure —
            // that classification is reserved for shell-level launch failures.
            Assert.IsNotType<CliInfrastructureException>(ex);
        }
        finally { CleanupWorkspace(workspace); }
    }

    [Theory]
    [InlineData(126)] // permission denied
    [InlineData(127)] // command not found
    public async Task ExecuteAsync_ShellLaunchFailureExitCode_ThrowsInfrastructureException(int exitCode)
    {
        var workspace = NewWorkspace();
        try
        {
            var executor = CreateExecutor(StubRunner(
                exitCode: exitCode, stdout: "", stderr: "bash: claude: " +
                    (exitCode == 126 ? "permission denied" : "command not found")));

            var ex = await Assert.ThrowsAsync<CliInfrastructureException>(
                () => executor.ExecuteAsync(CreateContext(workspace), CancellationToken.None));

            Assert.Contains($"exited with code {exitCode}", ex.Message);
        }
        finally { CleanupWorkspace(workspace); }
    }

    [Fact]
    public async Task ExecuteAsync_EmptyStdout_WithRateLimitStderr_ThrowsRateLimitException()
    {
        var workspace = NewWorkspace();
        try
        {
            var executor = CreateExecutor(StubRunner(
                exitCode: 0, stdout: "", stderr: "rate limit reached"));

            var ex = await Assert.ThrowsAsync<RateLimitException>(
                () => executor.ExecuteAsync(CreateContext(workspace), CancellationToken.None));

            Assert.Equal(RateLimitSource.AgentCli, ex.Source);
        }
        finally { CleanupWorkspace(workspace); }
    }

    [Fact]
    public async Task ExecuteAsync_EmptyStdout_NoRateLimit_ThrowsInvalidOperation()
    {
        var workspace = NewWorkspace();
        try
        {
            var executor = CreateExecutor(StubRunner(
                exitCode: 0, stdout: "", stderr: "something happened"));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => executor.ExecuteAsync(CreateContext(workspace), CancellationToken.None));

            Assert.Contains("empty output", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { CleanupWorkspace(workspace); }
    }

    [Fact]
    public async Task ExecuteAsync_ProcessRunnerThrowsTimeout_PropagatesTimeoutException()
    {
        var workspace = NewWorkspace();
        try
        {
            ProcessRunnerDelegate runner =
                (exe, args, wd, t, ct, stdin, remove, name, _)
                    => throw new TimeoutException("fake timeout");

            var executor = CreateExecutor(runner);

            await Assert.ThrowsAsync<TimeoutException>(
                () => executor.ExecuteAsync(CreateContext(workspace), CancellationToken.None));
        }
        finally { CleanupWorkspace(workspace); }
    }

    [Fact]
    public async Task ExecuteAsync_ProcessRunnerThrowsCancellation_PropagatesCancellation()
    {
        var workspace = NewWorkspace();
        try
        {
            ProcessRunnerDelegate runner =
                (exe, args, wd, t, ct, stdin, remove, name, _)
                    => throw new OperationCanceledException("cancelled");

            var executor = CreateExecutor(runner);

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => executor.ExecuteAsync(CreateContext(workspace), CancellationToken.None));
        }
        finally { CleanupWorkspace(workspace); }
    }

    [Fact]
    public async Task ExecuteAsync_AgentReportsError_ReturnsErrorOutcome()
    {
        var workspace = NewWorkspace();
        try
        {
            var stdout = BuildErrorOutcomeStdout("Something went wrong in the agent.");
            var executor = CreateExecutor(StubRunner(exitCode: 0, stdout: stdout, stderr: ""));

            var result = await executor.ExecuteAsync(CreateContext(workspace), CancellationToken.None);

            Assert.Equal(AgentOutcome.ERROR, result.Outcome);
            Assert.Equal("Something went wrong in the agent.", result.Detail);
        }
        finally { CleanupWorkspace(workspace); }
    }

    [Fact]
    public async Task ExecuteAsync_PipesUserPromptToSubprocessStdin()
    {
        // Regression guard: an executor that silently drops stdin would still
        // look fine against the outcome-based tests, but would break the actual
        // subprocess. Capture what the runner is called with.
        var workspace = NewWorkspace();
        try
        {
            string? observedStdin = null;
            ProcessRunnerDelegate runner =
                (exe, args, wd, t, ct, stdin, remove, name, _) =>
                {
                    observedStdin = stdin;
                    return Task.FromResult((0, BuildCompleteStdout("ok"), ""));
                };

            var executor = CreateExecutor(runner);
            var context = CreateContext(workspace);

            await executor.ExecuteAsync(context, CancellationToken.None);

            Assert.NotNull(observedStdin);
            // The user prompt (or the combined system+task prompt) must be passed
            // through stdin. Each executor wraps it differently, but the original
            // task prompt text must survive.
            Assert.Contains(context.TaskPrompt, observedStdin);
        }
        finally { CleanupWorkspace(workspace); }
    }
}
