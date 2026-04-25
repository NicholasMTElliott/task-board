using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

/// <summary>
/// OpenCode-specific diagnostics: output-parser fallback strategies, the
/// malformed-output retry loop, and stderr hint detection.
/// </summary>
public class DockerOpenCodeAgentExecutorDiagnosticsTests
{
    private static DockerOpenCodeAgentExecutor CreateExecutor(
        ProcessRunnerDelegate runner,
        int maxRetries = 0) =>
        new(
            Options.Create(new DockerOpenCodeAgentOptions
            {
                ImageName = "aiboard-opencode-test:latest",
                TimeoutSeconds = 30,
                MaxRetriesOnMalformedOutput = maxRetries,
            }),
            Helpers.TestTenant.Instance,
            NullLogger<DockerOpenCodeAgentExecutor>.Instance,
            mountBuilder: null,
            processRunner: runner);

    private static (string Workspace, string SystemPromptFile) NewWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "opencode-diag-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var promptFile = Path.Combine(dir, "system.md");
        File.WriteAllText(promptFile, "# System\nTest.");
        return (dir, promptFile);
    }

    private static void CleanupWorkspace(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    private static AgentExecutionContext CreateContext(string ws, string prompt) =>
        new(
            TargetCardId: "42",
            TargetCardTitle: "Test",
            WorkspacePath: ws,
            TaskPrompt: "do the thing",
            SystemPromptFilePath: prompt,
            Model: "qwen3.6-35b-a3b");

    // ── Parser strategy 1: fenced JSON ────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_FencedJsonBlock_ParsedAsStructuredOutput()
    {
        var (ws, promptFile) = NewWorkspace();
        try
        {
            var stdout = "Working on it now...\n\n```json\n" +
                "{\"outcome\":\"COMPLETE\",\"detail\":\"done\"}\n" +
                "```\n\nHope that helps!";

            var executor = CreateExecutor((exe, args, wd, t, ct, stdin, rm, n)
                => Task.FromResult((0, stdout, "")));

            var result = await executor.ExecuteAsync(
                CreateContext(ws, promptFile), CancellationToken.None);

            Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
            Assert.Equal("done", result.Detail);
        }
        finally { CleanupWorkspace(ws); }
    }

    // ── Parser strategy 2: whole-document JSON ────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WholeDocumentJson_ParsedAsStructuredOutput()
    {
        var (ws, promptFile) = NewWorkspace();
        try
        {
            var stdout = "{\"outcome\":\"NEEDS_INFO\",\"detail\":\"need more info\"}";

            var executor = CreateExecutor((exe, args, wd, t, ct, stdin, rm, n)
                => Task.FromResult((0, stdout, "")));

            var result = await executor.ExecuteAsync(
                CreateContext(ws, promptFile), CancellationToken.None);

            Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
            Assert.Equal("need more info", result.Detail);
        }
        finally { CleanupWorkspace(ws); }
    }

    // ── Parser strategy 3: last balanced JSON object ──────────────────────

    [Fact]
    public async Task ExecuteAsync_TrailingInlineJson_ParsedAsStructuredOutput()
    {
        var (ws, promptFile) = NewWorkspace();
        try
        {
            // Model narrates in prose and emits the JSON without fences at the end.
            var stdout = "Here's a quick thought: { \"not_outcome\": 1 } — but my final answer is:\n" +
                         "{\"outcome\":\"COMPLETE\",\"detail\":\"shipped\"}";

            var executor = CreateExecutor((exe, args, wd, t, ct, stdin, rm, n)
                => Task.FromResult((0, stdout, "")));

            var result = await executor.ExecuteAsync(
                CreateContext(ws, promptFile), CancellationToken.None);

            Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
            Assert.Equal("shipped", result.Detail);
        }
        finally { CleanupWorkspace(ws); }
    }

    // ── Retry loop: malformed → malformed → ERROR with raw output ─────────

    [Fact]
    public async Task ExecuteAsync_OutputNeverParseableAndRetriesExhausted_ReturnsErrorWithRawOutput()
    {
        var (ws, promptFile) = NewWorkspace();
        try
        {
            var callCount = 0;
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n) =>
            {
                callCount++;
                return Task.FromResult((0, "Just some prose, no JSON here.", ""));
            };

            // 1 retry = 2 total invocations
            var executor = CreateExecutor(runner, maxRetries: 1);

            var result = await executor.ExecuteAsync(
                CreateContext(ws, promptFile), CancellationToken.None);

            Assert.Equal(AgentOutcome.ERROR, result.Outcome);
            Assert.Equal(2, callCount);
            Assert.Contains("no parseable Agent Contract JSON",
                result.Detail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Just some prose", result.Detail);
        }
        finally { CleanupWorkspace(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_RateLimitOnRetryAttempt_ThrowsRateLimitException()
    {
        // Real-world failure mode the bare retry loop would miss: the upstream
        // server starts rate-limiting mid-run. It returns exit 0 with prose ("I
        // can't help right now") and "rate limit reached" on stderr. Without
        // the explicit check on the parse-failure path, we'd retry-then-fail
        // and silently classify a rate limit as a parse error, costing both
        // tokens and an incorrect agent_run.failure_reason.
        var (ws, promptFile) = NewWorkspace();
        try
        {
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n)
                => Task.FromResult((0,
                    "Sorry, I cannot answer right now. Please try again later.",
                    "openai: rate limit reached for model"));

            var executor = CreateExecutor(runner, maxRetries: 2);

            var ex = await Assert.ThrowsAsync<RateLimitException>(
                () => executor.ExecuteAsync(CreateContext(ws, promptFile), CancellationToken.None));

            Assert.Equal(RateLimitSource.AgentCli, ex.Source);
        }
        finally { CleanupWorkspace(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_FirstAttemptMalformed_SecondAttemptValid_Succeeds()
    {
        var (ws, promptFile) = NewWorkspace();
        try
        {
            var callCount = 0;
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n) =>
            {
                callCount++;
                if (callCount == 1)
                    return Task.FromResult((0, "garbled prose", ""));
                return Task.FromResult((0,
                    "```json\n{\"outcome\":\"COMPLETE\",\"detail\":\"fixed on retry\"}\n```", ""));
            };

            var executor = CreateExecutor(runner, maxRetries: 2);

            var result = await executor.ExecuteAsync(
                CreateContext(ws, promptFile), CancellationToken.None);

            Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
            Assert.Equal("fixed on retry", result.Detail);
            Assert.Equal(2, callCount);
        }
        finally { CleanupWorkspace(ws); }
    }

    // ── Stderr hint detection surfaces in exception messages ──────────────

    [Theory]
    [InlineData("Error: network llm-net not found", "Network")]
    [InlineData("curl: (6) could not resolve host: llama-server", "Network")]
    [InlineData("connection refused on port 8080", "Network")]
    [InlineData("llama-server returned 404", "Config")]
    [InlineData("model not found: qwen3.6-bogus", "Model")]
    public async Task ExecuteAsync_StderrMatchesKnownSignature_HintIncludedInException(
        string stderr, string expectedCategory)
    {
        var (ws, promptFile) = NewWorkspace();
        try
        {
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n)
                => Task.FromResult((1, "", stderr));

            var executor = CreateExecutor(runner);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => executor.ExecuteAsync(CreateContext(ws, promptFile), CancellationToken.None));

            Assert.Contains("[Hint]", ex.Message);
            Assert.Contains(expectedCategory, ex.Message);
        }
        finally { CleanupWorkspace(ws); }
    }

    // ── Docker-level exit codes classified as infrastructure ──────────────

    [Theory]
    [InlineData(125)] // Docker daemon error
    [InlineData(137)] // SIGKILL (OOM / forced stop)
    public async Task ExecuteAsync_DockerDaemonExitCode_ThrowsInfrastructureException(int exitCode)
    {
        var (ws, promptFile) = NewWorkspace();
        try
        {
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n)
                => Task.FromResult((exitCode, "", "docker: something went wrong"));

            var executor = CreateExecutor(runner);

            var ex = await Assert.ThrowsAsync<CliInfrastructureException>(
                () => executor.ExecuteAsync(CreateContext(ws, promptFile), CancellationToken.None));

            Assert.Contains($"exited with code {exitCode}", ex.Message);
        }
        finally { CleanupWorkspace(ws); }
    }

    // ── Docker args include --network llm-net by default ──────────────────

    [Fact]
    public void BuildDockerArgumentList_IncludesNetworkLlmNetByDefault()
    {
        var executor = new DockerOpenCodeAgentExecutor(
            Options.Create(new DockerOpenCodeAgentOptions()),
            Helpers.TestTenant.Instance,
            NullLogger<DockerOpenCodeAgentExecutor>.Instance);

        var args = executor.BuildDockerArgumentList(
            containerName: "aiboard-oc-test",
            hostPromptDir: "",
            openCodeArgs: new[] { "run" },
            mountContext: null);

        var argString = string.Join(" ", args);
        Assert.Contains("--network llm-net", argString);
    }

    [Fact]
    public void BuildDockerArgumentList_UsesOpenCodeSandboxImage()
    {
        var executor = new DockerOpenCodeAgentExecutor(
            Options.Create(new DockerOpenCodeAgentOptions()),
            Helpers.TestTenant.Instance,
            NullLogger<DockerOpenCodeAgentExecutor>.Instance);

        var args = executor.BuildDockerArgumentList(
            containerName: "aiboard-oc-test",
            hostPromptDir: "",
            openCodeArgs: new[] { "run" },
            mountContext: null);

        Assert.Contains("aiboard-opencode-sandbox:latest", args);
        Assert.Contains("opencode", args);
    }

    [Fact]
    public void BuildContainerName_IncludesTenantHashAndOcPrefix()
    {
        var executor = new DockerOpenCodeAgentExecutor(
            Options.Create(new DockerOpenCodeAgentOptions()),
            Helpers.TestTenant.Instance,
            NullLogger<DockerOpenCodeAgentExecutor>.Instance);

        var name = executor.BuildContainerName("42");

        // Expected shape: aiboard-oc-{tenantHash}-{cardId}-{rand8}
        Assert.StartsWith("aiboard-oc-deadbeef-42-", name);
    }
}
