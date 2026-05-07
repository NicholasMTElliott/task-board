using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Tests.Helpers;

namespace TaskBoard.Worker.Tests.Clients;

/// <summary>
/// OpenCode-specific diagnostics: output-parser fallback strategies, the
/// malformed-output retry loop, and stderr hint detection.
/// </summary>
public class DockerOpenCodeAgentExecutorDiagnosticsTests
{
    private static DockerOpenCodeAgentExecutor CreateExecutor(
        ProcessRunnerDelegate runner,
        int maxRetries = 0,
        bool enableStructurer = false,
        string? structurerModelName = null) =>
        new(
            TestOptionsMonitor.Create(new DockerOpenCodeAgentOptions
            {
                ImageName = "aiboard-opencode-test:latest",
                TimeoutSeconds = 30,
                MaxRetriesOnMalformedOutput = maxRetries,
                // Opt-out by default in tests so the bulk of the suite
                // exercises the existing single-call path without the new
                // structurer interfering. Production default is true; tests
                // that exercise the structurer pass enableStructurer: true.
                EnableStructurer = enableStructurer,
                StructurerModelName = structurerModelName ?? "qwen3.6-35b-a3b",
                StructurerTimeoutSeconds = 5,
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

            var executor = CreateExecutor((exe, args, wd, t, ct, stdin, rm, n, _)
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

            var executor = CreateExecutor((exe, args, wd, t, ct, stdin, rm, n, _)
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

            var executor = CreateExecutor((exe, args, wd, t, ct, stdin, rm, n, _)
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
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _) =>
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
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _)
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
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _) =>
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
    // 404 is its own "Path" category: a 404 reaching the SDK means the proxy
    // AND backend are running (otherwise we'd see ENOTFOUND or 502), so it's
    // a real path mismatch — most likely an SDK probe call to an endpoint
    // llama.cpp doesn't expose (`/v1/embeddings`, `/v1/models/{id}`, etc.).
    // The previous "Config / wrong URL" categorization sent diagnosis the wrong
    // way: the URL config is verified-correct against local-llm's wire contract.
    [InlineData("llama-server returned 404", "Path")]
    [InlineData("upstream unreachable: 502 Bad Gateway", "Network")]
    public async Task ExecuteAsync_FatalHintCategory_ThrowsInfrastructureException(
        string stderr, string expectedCategory)
    {
        // Network / Auth / Config / Path are unrecoverable from a re-prompt's
        // perspective — bail with INFRASTRUCTURE failure-reason so callers
        // (AgentRunner, candidate slot loop) can route appropriately.
        var (ws, promptFile) = NewWorkspace();
        try
        {
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _)
                => Task.FromResult((1, "", stderr));

            var executor = CreateExecutor(runner);

            var ex = await Assert.ThrowsAsync<CliInfrastructureException>(
                () => executor.ExecuteAsync(CreateContext(ws, promptFile), CancellationToken.None));

            Assert.Contains("[Hint]", ex.Message);
            Assert.Contains(expectedCategory, ex.Message);
        }
        finally { CleanupWorkspace(ws); }
    }

    [Theory]
    [InlineData("model not found: qwen3.6-bogus", "Model")]
    public async Task ExecuteAsync_NonFatalHintCategory_StillThrowsRegularException(
        string stderr, string expectedCategory)
    {
        // Model is NOT in FatalHintCategories — a model could be loaded mid-run
        // (rare but theoretically recoverable), so we DON'T short-circuit. The
        // exit=1 still fails the run, but with InvalidOperationException
        // (categorised as AGENT_ERROR), not CliInfrastructureException.
        // Regression guard against accidentally widening FatalHintCategories.
        var (ws, promptFile) = NewWorkspace();
        try
        {
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _)
                => Task.FromResult((1, "", stderr));

            var executor = CreateExecutor(runner);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => executor.ExecuteAsync(CreateContext(ws, promptFile), CancellationToken.None));

            Assert.Contains("[Hint]", ex.Message);
            Assert.Contains(expectedCategory, ex.Message);
        }
        finally { CleanupWorkspace(ws); }
    }

    // ── Fatal-hint short-circuit on the exit=0 + unparseable path ─────────

    [Fact]
    public async Task ExecuteAsync_NetworkHint502_ExitZeroProse_BailsBeforeRetry()
    {
        // The original KvA failure shape: llama-server proxy returned
        // 502 mid-run; OpenCode CLI exited 0 with prose ("upstream unreachable")
        // and "502" in stderr. Without the bail, the retry-on-malformed-output
        // loop burned 3 × inactivity-timer (~60 min) before surfacing.
        //
        // Pass criteria: exactly ONE invocation of the runner, then
        // CliInfrastructureException — no retries.
        var (ws, promptFile) = NewWorkspace();
        try
        {
            var callCount = 0;
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _) =>
            {
                callCount++;
                return Task.FromResult((0,
                    "I cannot answer right now — upstream returned an error.",
                    "HTTP 502 Bad Gateway from llama-server"));
            };

            var executor = CreateExecutor(runner, maxRetries: 3);

            var ex = await Assert.ThrowsAsync<CliInfrastructureException>(
                () => executor.ExecuteAsync(CreateContext(ws, promptFile), CancellationToken.None));

            Assert.Equal(1, callCount);
            Assert.Contains("Network", ex.Message);
            Assert.Contains("[Hint]", ex.Message);
        }
        finally { CleanupWorkspace(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_AuthHint_BailsBeforeRetry()
    {
        var (ws, promptFile) = NewWorkspace();
        try
        {
            var callCount = 0;
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _) =>
            {
                callCount++;
                return Task.FromResult((0, "denied", "401 Unauthorized from proxy"));
            };

            var executor = CreateExecutor(runner, maxRetries: 3);

            var ex = await Assert.ThrowsAsync<CliInfrastructureException>(
                () => executor.ExecuteAsync(CreateContext(ws, promptFile), CancellationToken.None));

            Assert.Equal(1, callCount);
            Assert.Contains("Auth", ex.Message);
        }
        finally { CleanupWorkspace(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_PathHint404_BailsBeforeRetry()
    {
        var (ws, promptFile) = NewWorkspace();
        try
        {
            var callCount = 0;
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _) =>
            {
                callCount++;
                return Task.FromResult((0, "request failed",
                    "llama-server returned 404 for /v1/embeddings"));
            };

            var executor = CreateExecutor(runner, maxRetries: 3);

            var ex = await Assert.ThrowsAsync<CliInfrastructureException>(
                () => executor.ExecuteAsync(CreateContext(ws, promptFile), CancellationToken.None));

            Assert.Equal(1, callCount);
            Assert.Contains("Path", ex.Message);
        }
        finally { CleanupWorkspace(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_ConfigHint_BailsBeforeRetry()
    {
        var (ws, promptFile) = NewWorkspace();
        try
        {
            var callCount = 0;
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _) =>
            {
                callCount++;
                return Task.FromResult((0, "no result", "provider not found: bogus-key"));
            };

            var executor = CreateExecutor(runner, maxRetries: 3);

            var ex = await Assert.ThrowsAsync<CliInfrastructureException>(
                () => executor.ExecuteAsync(CreateContext(ws, promptFile), CancellationToken.None));

            Assert.Equal(1, callCount);
            Assert.Contains("Config", ex.Message);
        }
        finally { CleanupWorkspace(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_NonFatalHint_StillRetries_RegressionGuard()
    {
        // "model not found" is a Model-category hint. Model is NOT in
        // FatalHintCategories, so we should NOT short-circuit — retries
        // continue (the model could be loaded mid-run on a slow llama-server).
        // This pins that the fatal list isn't accidentally widened.
        var (ws, promptFile) = NewWorkspace();
        try
        {
            var callCount = 0;
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _) =>
            {
                callCount++;
                return Task.FromResult((0, "garbled prose, no JSON",
                    "warning: model not found in catalog"));
            };

            var executor = CreateExecutor(runner, maxRetries: 1);

            // 2 invocations (initial + 1 retry), then ERROR with raw output.
            var result = await executor.ExecuteAsync(
                CreateContext(ws, promptFile), CancellationToken.None);

            Assert.Equal(2, callCount);
            Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        }
        finally { CleanupWorkspace(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_NoHint_StillRetries_RegressionGuard()
    {
        // No stderr hint at all → original retry-on-malformed-output behavior
        // unchanged. Pins that the fatal-hint short-circuit doesn't fire on
        // a null hint (defensive against a hint-detector misclassification).
        var (ws, promptFile) = NewWorkspace();
        try
        {
            var callCount = 0;
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _) =>
            {
                callCount++;
                return Task.FromResult((0, "garbled prose", ""));
            };

            var executor = CreateExecutor(runner, maxRetries: 1);

            var result = await executor.ExecuteAsync(
                CreateContext(ws, promptFile), CancellationToken.None);

            Assert.Equal(2, callCount);
            Assert.Equal(AgentOutcome.ERROR, result.Outcome);
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
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _)
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
            TestOptionsMonitor.Create(new DockerOpenCodeAgentOptions()),
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
            TestOptionsMonitor.Create(new DockerOpenCodeAgentOptions()),
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
            TestOptionsMonitor.Create(new DockerOpenCodeAgentOptions()),
            Helpers.TestTenant.Instance,
            NullLogger<DockerOpenCodeAgentExecutor>.Instance);

        var name = executor.BuildContainerName("42");

        // Expected shape: aiboard-oc-{tenantHash}-{cardId}-{rand8}
        Assert.StartsWith("aiboard-oc-deadbeef-42-", name);
    }

    // ── No-think Qwen structurer fallback (v0.0.22+) ───────────────────────

    /// <summary>
    /// Distinguishes structurer calls from main-agent calls by inspecting the
    /// container name passed via <c>--name</c>. Structurer containers carry a
    /// <c>-struct-</c> infix (see <c>BuildStructurerContainerName</c>).
    /// </summary>
    private static bool IsStructurerCall(string[] dockerArgs)
    {
        for (var i = 0; i < dockerArgs.Length - 1; i++)
        {
            if (dockerArgs[i] == "--name"
                && dockerArgs[i + 1].Contains("-struct-", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    [Fact]
    public async Task ExecuteAsync_StructurerEnabled_RecoversFromProseNarrative_NoRetry()
    {
        // Headline scenario: the KvA failure shape — agent narrates
        // correct work in prose but skips the JSON envelope. Structurer takes
        // the narrative, returns parseable JSON. Executor returns recovered
        // result without consuming a retry attempt.
        var (ws, promptFile) = NewWorkspace();
        try
        {
            var mainCalls = 0;
            var structurerCalls = 0;
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _) =>
            {
                if (IsStructurerCall(args))
                {
                    structurerCalls++;
                    return Task.FromResult((0,
                        "```json\n{\"outcome\":\"COMPLETE\",\"detail\":\"Recovered: agent finished implementation\"}\n```",
                        ""));
                }
                mainCalls++;
                return Task.FromResult((0,
                    "I read the code and the task is already complete. " +
                    "main_menu.gd has all the live text buttons wired up correctly.",
                    ""));
            };

            var executor = CreateExecutor(runner, maxRetries: 2, enableStructurer: true);

            var result = await executor.ExecuteAsync(
                CreateContext(ws, promptFile), CancellationToken.None);

            Assert.Equal(1, mainCalls);
            Assert.Equal(1, structurerCalls);
            Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
            Assert.Equal("Recovered: agent finished implementation", result.Detail);
            Assert.Contains("Recovered via no-think structurer", result.ConversationLog);
        }
        finally { CleanupWorkspace(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_StructurerAlsoFails_FallsThroughToRetryLoop()
    {
        // Belt-and-braces: when the structurer ALSO produces unparseable
        // output, we don't get stuck — execution falls through to the
        // existing retry-with-stricter-reprompt path.
        var (ws, promptFile) = NewWorkspace();
        try
        {
            var mainCalls = 0;
            var structurerCalls = 0;
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _) =>
            {
                if (IsStructurerCall(args))
                {
                    structurerCalls++;
                    return Task.FromResult((0,
                        "I cannot determine the outcome from the narrative.",
                        ""));
                }
                mainCalls++;
                return Task.FromResult((0, "narrative without JSON", ""));
            };

            var executor = CreateExecutor(runner, maxRetries: 1, enableStructurer: true);

            var result = await executor.ExecuteAsync(
                CreateContext(ws, promptFile), CancellationToken.None);

            // 1 initial + 1 retry = 2 main calls. Structurer fired once on attempt 0 only.
            Assert.Equal(2, mainCalls);
            Assert.Equal(1, structurerCalls);
            Assert.Equal(AgentOutcome.ERROR, result.Outcome);
            Assert.Contains("no parseable Agent Contract JSON", result.Detail);
        }
        finally { CleanupWorkspace(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_StructurerDisabled_GoesStraightToRetry()
    {
        // Operator opt-out: EnableStructurer=false reverts to the pre-structurer
        // behavior — re-prompt the same model with stricter instructions.
        var (ws, promptFile) = NewWorkspace();
        try
        {
            var mainCalls = 0;
            var structurerCalls = 0;
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _) =>
            {
                if (IsStructurerCall(args)) { structurerCalls++; }
                else { mainCalls++; }
                return Task.FromResult((0, "narrative without JSON", ""));
            };

            var executor = CreateExecutor(runner, maxRetries: 1, enableStructurer: false);

            var result = await executor.ExecuteAsync(
                CreateContext(ws, promptFile), CancellationToken.None);

            Assert.Equal(0, structurerCalls);
            Assert.Equal(2, mainCalls); // initial + 1 retry
            Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        }
        finally { CleanupWorkspace(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_StructurerOnlyFiresOnFirstAttempt()
    {
        // Pin: the structurer is a one-shot fallback, not a per-retry helper.
        // After the first structurer attempt fails, subsequent retries do NOT
        // re-invoke it — that would multiply the cost on hopeless prompts.
        var (ws, promptFile) = NewWorkspace();
        try
        {
            var mainCalls = 0;
            var structurerCalls = 0;
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _) =>
            {
                if (IsStructurerCall(args)) { structurerCalls++; }
                else { mainCalls++; }
                return Task.FromResult((0, "still no JSON", ""));
            };

            var executor = CreateExecutor(runner, maxRetries: 3, enableStructurer: true);

            var result = await executor.ExecuteAsync(
                CreateContext(ws, promptFile), CancellationToken.None);

            // 4 main calls (1 initial + 3 retries), 1 structurer call (on attempt 0 only).
            Assert.Equal(4, mainCalls);
            Assert.Equal(1, structurerCalls);
            Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        }
        finally { CleanupWorkspace(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_OutputParseable_StructurerNotInvoked()
    {
        // Performance regression guard: when the original output IS parseable,
        // the structurer must NOT be invoked. Cost matters — every container
        // spin-up adds 30-60s + token spend.
        var (ws, promptFile) = NewWorkspace();
        try
        {
            var mainCalls = 0;
            var structurerCalls = 0;
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _) =>
            {
                if (IsStructurerCall(args)) { structurerCalls++; }
                else { mainCalls++; }
                return Task.FromResult((0,
                    "```json\n{\"outcome\":\"COMPLETE\",\"detail\":\"first try worked\"}\n```",
                    ""));
            };

            var executor = CreateExecutor(runner, maxRetries: 2, enableStructurer: true);

            var result = await executor.ExecuteAsync(
                CreateContext(ws, promptFile), CancellationToken.None);

            Assert.Equal(1, mainCalls);
            Assert.Equal(0, structurerCalls);
            Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
            Assert.Equal("first try worked", result.Detail);
        }
        finally { CleanupWorkspace(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_StructurerTimesOut_FallsThroughToRetry()
    {
        // Robustness: structurer infrastructure failures (timeout, container
        // crash) must not propagate up — they fall through to the existing
        // retry path so the operator gets the same final outcome they would
        // have without the structurer enabled.
        var (ws, promptFile) = NewWorkspace();
        try
        {
            var mainCalls = 0;
            var structurerCalls = 0;
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _) =>
            {
                if (IsStructurerCall(args))
                {
                    structurerCalls++;
                    throw new TimeoutException(
                        "Docker/OpenCode structurer timed out after 5s. Partial stderr: ");
                }
                mainCalls++;
                return Task.FromResult((0, "narrative without JSON", ""));
            };

            var executor = CreateExecutor(runner, maxRetries: 1, enableStructurer: true);

            var result = await executor.ExecuteAsync(
                CreateContext(ws, promptFile), CancellationToken.None);

            Assert.Equal(1, structurerCalls);
            Assert.Equal(2, mainCalls); // initial + 1 retry — structurer didn't crash the run
            Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        }
        finally { CleanupWorkspace(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_StructurerExitNonZero_FallsThroughToRetry()
    {
        var (ws, promptFile) = NewWorkspace();
        try
        {
            var mainCalls = 0;
            var structurerCalls = 0;
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _) =>
            {
                if (IsStructurerCall(args))
                {
                    structurerCalls++;
                    return Task.FromResult((1, "", "structurer container crashed"));
                }
                mainCalls++;
                return Task.FromResult((0, "narrative without JSON", ""));
            };

            var executor = CreateExecutor(runner, maxRetries: 1, enableStructurer: true);

            var result = await executor.ExecuteAsync(
                CreateContext(ws, promptFile), CancellationToken.None);

            Assert.Equal(1, structurerCalls);
            Assert.Equal(2, mainCalls);
            Assert.Equal(AgentOutcome.ERROR, result.Outcome);
        }
        finally { CleanupWorkspace(ws); }
    }

    [Fact]
    public async Task ExecuteAsync_StructurerInfersNeedsInfo_PropagatesQuestions()
    {
        // The structurer should recognize question-asking narratives and emit
        // outcome=NEEDS_INFO with questions[] rather than COMPLETE. This is the
        // path that lets human-in-the-loop questions survive even when the
        // original agent forgot to emit the JSON envelope.
        var (ws, promptFile) = NewWorkspace();
        try
        {
            var mainCalls = 0;
            var structurerCalls = 0;
            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, rm, n, _) =>
            {
                if (IsStructurerCall(args))
                {
                    structurerCalls++;
                    return Task.FromResult((0,
                        "```json\n" +
                        "{\"outcome\":\"NEEDS_INFO\",\"detail\":\"Agent asked for clarification\"," +
                        "\"questions\":[{\"question\":\"Should the menu use serif or sans-serif font?\",\"recommendations\":[\"Cinzel\",\"Inter\"]}]}\n" +
                        "```",
                        ""));
                }
                mainCalls++;
                return Task.FromResult((0,
                    "I started looking at main_menu.gd but realized I need to know " +
                    "whether you want the buttons to use a serif font like Cinzel " +
                    "or a sans-serif like Inter. Can you confirm?",
                    ""));
            };

            var executor = CreateExecutor(runner, maxRetries: 2, enableStructurer: true);

            var result = await executor.ExecuteAsync(
                CreateContext(ws, promptFile), CancellationToken.None);

            Assert.Equal(1, mainCalls);
            Assert.Equal(1, structurerCalls);
            Assert.Equal(AgentOutcome.NEEDS_INFO, result.Outcome);
            Assert.NotNull(result.Questions);
            Assert.Single(result.Questions);
            Assert.Contains("serif", result.Questions[0].Question, StringComparison.OrdinalIgnoreCase);
        }
        finally { CleanupWorkspace(ws); }
    }

    // ── BuildStructurerPrompt: regression guard for the model's contract ──

    [Fact]
    public void BuildStructurerPrompt_ContainsSchemaAndNarrative()
    {
        const string narrative = "I read main_menu.gd and verified the buttons are present. Task is complete.";
        var prompt = DockerOpenCodeAgentExecutor.BuildStructurerPrompt(narrative);

        // The narrative must be passed verbatim under a clearly-marked header.
        Assert.Contains("## Narrative to Structure", prompt);
        Assert.Contains(narrative, prompt);

        // The schema must appear in a JSON code fence — the structurer needs to
        // see the contract shape it's extracting against.
        Assert.Contains("```json", prompt);
        Assert.Contains("\"outcome\"", prompt);

        // Outcome enum values must appear so the model knows the valid set.
        Assert.Contains("COMPLETE", prompt);
        Assert.Contains("NEEDS_INFO", prompt);
        Assert.Contains("ERROR", prompt);
    }

    [Fact]
    public void BuildStructurerPrompt_DoesNotIncludeOriginalTaskOrSystemPrompt()
    {
        // Critical regression guard: the structurer must see ONLY the narrative
        // it is structuring, not the original task or system prompt. Otherwise
        // it could hallucinate fields based on the task description rather than
        // the agent's actual narrative output. The user-facing risk:
        // structurer says COMPLETE because the task SAID to do X, even though
        // the narrative didn't actually finish X.
        const string narrative = "I started but didn't finish.";
        var prompt = DockerOpenCodeAgentExecutor.BuildStructurerPrompt(narrative);

        // The structurer prompt is a closed system: schema + rules + narrative.
        // It must not include task-prompt headers or shared-section markers
        // that the main-agent prompt builder injects.
        Assert.DoesNotContain("## Task\n", prompt);
        Assert.DoesNotContain("## System Instructions", prompt);
        Assert.DoesNotContain("## Card Body", prompt);
        Assert.DoesNotContain("## Comments", prompt);
    }

    [Fact]
    public void BuildStructurerPrompt_ForbidsReasoningOutput()
    {
        // The structurer is invoked against the no-think Qwen variant precisely
        // because reasoning would bury the JSON output. The prompt must
        // explicitly forbid reasoning so even the -think variant (if an operator
        // overrides StructurerModelName) doesn't pollute the output.
        var prompt = DockerOpenCodeAgentExecutor.BuildStructurerPrompt("any narrative");

        Assert.Contains("Output ONLY the JSON object", prompt);
        Assert.Contains("Do NOT", prompt); // covers "Do NOT add fields" + "Do NOT include reasoning"
        // Faithfulness instruction — guards against hallucinating fields.
        Assert.Contains("do NOT invent", prompt, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildStructurerDockerArgumentList_AppendsModelOverride_BeforeImage()
    {
        // The override env var must come AFTER the mountContext's env vars
        // (which set OPENCODE_MODEL_NAME based on the role's effective model)
        // and BEFORE the image name. Docker honors the LAST -e for a duplicate
        // key, so this ordering is what makes the no-think model take effect.
        var executor = CreateExecutor(
            (_, _, _, _, _, _, _, _, _) => throw new InvalidOperationException("not invoked"),
            maxRetries: 0,
            enableStructurer: true,
            structurerModelName: "qwen3.6-35b-a3b");

        var args = executor.BuildStructurerDockerArgumentList(
            structurerContainerName: "aiboard-oc-struct-test-42-abc",
            hostPromptDir: "",
            mountContext: null);

        var imageIndex = Array.IndexOf(args, "aiboard-opencode-test:latest");
        Assert.True(imageIndex > 0, "image must appear in args");

        // Find the LAST -e OPENCODE_MODEL_NAME=... before the image
        var found = false;
        for (var i = 0; i < imageIndex - 1; i++)
        {
            if (args[i] == "-e"
                && args[i + 1] == "OPENCODE_MODEL_NAME=qwen3.6-35b-a3b")
            {
                found = true;
            }
        }
        Assert.True(found, "structurer args must include -e OPENCODE_MODEL_NAME=<no-think> before the image");

        // And the container --name must include -struct- so observability /
        // orphan detection treat it distinctly.
        var nameIdx = Array.IndexOf(args, "--name");
        Assert.True(nameIdx >= 0);
        Assert.Contains("-struct-", args[nameIdx + 1], StringComparison.Ordinal);
    }
}
