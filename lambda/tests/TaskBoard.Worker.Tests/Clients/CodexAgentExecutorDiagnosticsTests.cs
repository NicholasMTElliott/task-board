using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

/// <summary>
/// Covers the defensive diagnostics added to <see cref="CodexAgentExecutor"/>:
/// loud failure when stdout contains no <c>structured_output</c>, stderr hint
/// detection, and effective-policy logging via <see cref="BuildArgumentList"/>.
/// </summary>
public class CodexAgentExecutorDiagnosticsTests
{
    private static AgentExecutionContext CreateContext(string workspacePath, string systemPromptFile) =>
        new(
            TargetCardId: "42",
            TargetCardTitle: "Test",
            WorkspacePath: workspacePath,
            TaskPrompt: "do a thing",
            SystemPromptFilePath: systemPromptFile,
            Model: "codex-mini-latest");

    private static (string Workspace, string SystemPromptFile) NewWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "codex-diag-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var promptFile = Path.Combine(dir, "system.md");
        File.WriteAllText(promptFile, "# System\nTest.");
        return (dir, promptFile);
    }

    private static void CleanupWorkspace(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { }
    }

    // ── Loud failure: no structured_output in NDJSON stream ───────────────────

    [Fact]
    public async Task ExecuteAsync_NdjsonWithoutStructuredOutput_ThrowsWithDiagnostic()
    {
        var (workspace, promptFile) = NewWorkspace();
        try
        {
            // Simulate the common Codex misbehaviour: the stream emits thread.started
            // + a few item.completed events but never a turn.completed with structured_output.
            var stdout = string.Join('\n',
                """{"type":"thread.started","thread_id":"t-1"}""",
                """{"type":"item.completed","item":{"type":"output_text","text":"Thinking..."}}""",
                """{"type":"item.completed","item":{"type":"output_text","text":"Still thinking."}}"""
            );

            ProcessRunnerDelegate runner =
                (exe, args, wd, t, ct, stdin, remove, name)
                    => Task.FromResult((0, stdout, ""));

            var executor = new CodexAgentExecutor(
                Options.Create(new CodexCliLlmOptions()),
                NullLogger<CodexAgentExecutor>.Instance,
                runner);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => executor.ExecuteAsync(CreateContext(workspace, promptFile), CancellationToken.None));

            Assert.Contains("no structured_output", ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("diagnostic", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally { CleanupWorkspace(workspace); }
    }

    [Fact]
    public async Task ExecuteAsync_SingleDocumentJsonWithStructuredOutput_Accepted()
    {
        // Backward-compat path: some Codex versions emit a single JSON doc instead
        // of NDJSON. We accept that only when structured_output is present.
        var (workspace, promptFile) = NewWorkspace();
        try
        {
            var stdout = """{"structured_output":{"outcome":"COMPLETE","detail":"ok"}}""";

            ProcessRunnerDelegate runner =
                (exe, args, wd, t, ct, stdin, remove, name)
                    => Task.FromResult((0, stdout, ""));

            var executor = new CodexAgentExecutor(
                Options.Create(new CodexCliLlmOptions()),
                NullLogger<CodexAgentExecutor>.Instance,
                runner);

            var result = await executor.ExecuteAsync(
                CreateContext(workspace, promptFile), CancellationToken.None);

            Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
            Assert.Equal("ok", result.Detail);
        }
        finally { CleanupWorkspace(workspace); }
    }

    [Fact]
    public async Task ExecuteAsync_RawTextWithCompleteKeyword_ThrowsInsteadOfGuessing()
    {
        // Before the defensive pass, raw text containing "COMPLETE" would have
        // silently classified the run as COMPLETE via keyword scanning. That's
        // exactly the kind of silent success we want to prevent.
        var (workspace, promptFile) = NewWorkspace();
        try
        {
            var stdout = "Everything looks COMPLETE here, but this is not structured output.";

            ProcessRunnerDelegate runner =
                (exe, args, wd, t, ct, stdin, remove, name)
                    => Task.FromResult((0, stdout, ""));

            var executor = new CodexAgentExecutor(
                Options.Create(new CodexCliLlmOptions()),
                NullLogger<CodexAgentExecutor>.Instance,
                runner);

            await Assert.ThrowsAsync<InvalidOperationException>(
                () => executor.ExecuteAsync(CreateContext(workspace, promptFile), CancellationToken.None));
        }
        finally { CleanupWorkspace(workspace); }
    }

    // ── Stderr hint detection surfaces in the thrown exception ────────────────

    [Theory]
    [InlineData("OPENAI_API_KEY is not set", "Auth")]
    [InlineData("Error: invalid_api_key: The API key provided is invalid.", "Auth")]
    [InlineData("HTTP 401 Unauthorized from api.openai.com", "Auth")]
    [InlineData("model_not_found: 'o1-preview'", "Model")]
    [InlineData("error: unrecognized subcommand 'exec'", "VersionDrift")]
    [InlineData("error: unexpected argument '--output-schema'", "VersionDrift")]
    [InlineData("sandbox policy prevents writes to /etc", "Sandbox")]
    [InlineData("Failed to connect to api.openai.com", "Network")]
    public async Task ExecuteAsync_StderrMatchesKnownSignature_HintIncludedInException(
        string stderr, string expectedCategory)
    {
        var (workspace, promptFile) = NewWorkspace();
        try
        {
            ProcessRunnerDelegate runner =
                (exe, args, wd, t, ct, stdin, remove, name)
                    => Task.FromResult((1, "", stderr));

            var executor = new CodexAgentExecutor(
                Options.Create(new CodexCliLlmOptions()),
                NullLogger<CodexAgentExecutor>.Instance,
                runner);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => executor.ExecuteAsync(CreateContext(workspace, promptFile), CancellationToken.None));

            Assert.Contains("[Hint]", ex.Message);
            Assert.Contains(expectedCategory, ex.Message);
        }
        finally { CleanupWorkspace(workspace); }
    }

    [Fact]
    public async Task ExecuteAsync_ExceptionIncludesStderrBeyond500Chars()
    {
        // Old code capped stderr at 500 chars in exception messages, so the real
        // signal was sometimes lost. The cap is now 4000.
        var (workspace, promptFile) = NewWorkspace();
        try
        {
            var longStderr = new string('a', 600) + "REAL_ERROR_MARKER" + new string('b', 1000);

            ProcessRunnerDelegate runner =
                (exe, args, wd, t, ct, stdin, remove, name)
                    => Task.FromResult((1, "", longStderr));

            var executor = new CodexAgentExecutor(
                Options.Create(new CodexCliLlmOptions()),
                NullLogger<CodexAgentExecutor>.Instance,
                runner);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(
                () => executor.ExecuteAsync(CreateContext(workspace, promptFile), CancellationToken.None));

            Assert.Contains("REAL_ERROR_MARKER", ex.Message);
        }
        finally { CleanupWorkspace(workspace); }
    }

    // ── Effective-policy logging ──────────────────────────────────────────────

    [Fact]
    public void BuildArgumentList_LogsEffectivePolicyAtInfo()
    {
        var logger = new CapturingLogger<CodexAgentExecutor>();
        var executor = new CodexAgentExecutor(
            Options.Create(new CodexCliLlmOptions { FullAuto = true, Sandbox = "workspace-write" }),
            logger);

        var context = new AgentExecutionContext(
            TargetCardId: "1", TargetCardTitle: "T",
            WorkspacePath: "/tmp", TaskPrompt: "p",
            SystemPromptFilePath: "/tmp/s.md", Model: "codex-mini-latest");

        executor.BuildArgumentList(context, "/tmp/schema.json");

        Assert.Contains(logger.Messages,
            m => m.Contains("Codex effective policy", StringComparison.OrdinalIgnoreCase)
                 && m.Contains("fullAuto=True", StringComparison.OrdinalIgnoreCase)
                 && m.Contains("sandbox=workspace-write", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildArgumentList_LogsProviderParamsOverrideSource()
    {
        var logger = new CapturingLogger<CodexAgentExecutor>();
        var executor = new CodexAgentExecutor(
            Options.Create(new CodexCliLlmOptions { FullAuto = true }),
            logger);

        var context = new AgentExecutionContext(
            TargetCardId: "1", TargetCardTitle: "T",
            WorkspacePath: "/tmp", TaskPrompt: "p",
            SystemPromptFilePath: "/tmp/s.md", Model: "codex-mini-latest",
            ProviderParams: new Dictionary<string, string>
            {
                ["sandbox"] = "danger-full-access",
            });

        executor.BuildArgumentList(context, "/tmp/schema.json");

        // Verify the sandbox source is annotated as providerParams, not config.
        Assert.Contains(logger.Messages,
            m => m.Contains("sandbox=danger-full-access (providerParams)",
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void BuildArgumentList_YoloWithConflictingFlags_WarnsAboutIneffectiveFlags()
    {
        var logger = new CapturingLogger<CodexAgentExecutor>();
        var executor = new CodexAgentExecutor(
            Options.Create(new CodexCliLlmOptions
            {
                Yolo = true,
                FullAuto = true,
                Sandbox = "workspace-write",
            }),
            logger);

        var context = new AgentExecutionContext(
            TargetCardId: "1", TargetCardTitle: "T",
            WorkspacePath: "/tmp", TaskPrompt: "p",
            SystemPromptFilePath: "/tmp/s.md", Model: "codex-mini-latest");

        executor.BuildArgumentList(context, "/tmp/schema.json");

        Assert.Contains(logger.Messages,
            m => m.Contains("yolo is enabled", StringComparison.OrdinalIgnoreCase)
                 && m.Contains("have no effect", StringComparison.OrdinalIgnoreCase));
    }

    // ── Minimal capturing logger for Info/Warning assertions ──────────────────

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = new();

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
