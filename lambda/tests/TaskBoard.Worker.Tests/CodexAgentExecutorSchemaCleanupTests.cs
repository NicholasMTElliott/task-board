using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Tests.Helpers;

namespace TaskBoard.Worker.Tests;

/// <summary>
/// Regression tests for the <c>finally</c>-block cleanup of the Codex temp
/// schema file. The schema is written to <c>Path.GetTempPath()/codex-schema-{guid}.json</c>
/// before invoking the Codex CLI and must be deleted afterward on all exit paths —
/// including when the subprocess throws.
/// </summary>
public class CodexAgentExecutorSchemaCleanupTests
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
            "codex-schema-cleanup-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var promptFile = Path.Combine(dir, "system.md");
        File.WriteAllText(promptFile, "# System\nTest.");
        return (dir, promptFile);
    }

    private static int CountCodexSchemaFiles() =>
        Directory.EnumerateFiles(Path.GetTempPath(), "codex-schema-*.json").Count();

    [Fact]
    public async Task ExecuteAsync_Success_DeletesSchemaFile()
    {
        var (workspace, promptFile) = NewWorkspace();
        try
        {
            var before = CountCodexSchemaFiles();

            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, remove, name, _) =>
            {
                // The executor writes its schema to a temp file and passes the path
                // via `--output-schema`. Confirm the file exists DURING the subprocess.
                var schemaIdx = Array.IndexOf(args, "--output-schema");
                Assert.True(schemaIdx >= 0, "Expected --output-schema arg");
                var schemaPath = args[schemaIdx + 1];
                Assert.True(File.Exists(schemaPath),
                    "Schema file should exist while subprocess runs");
                return Task.FromResult((0,
                    """{"type":"turn.completed","structured_output":{"outcome":"COMPLETE","detail":"ok"}}""",
                    ""));
            };

            var executor = new CodexAgentExecutor(
                TestOptionsMonitor.Create(new CodexCliLlmOptions()),
                NullLogger<CodexAgentExecutor>.Instance,
                runner);

            await executor.ExecuteAsync(CreateContext(workspace, promptFile), CancellationToken.None);

            // After the call, temp-dir schema count must not grow.
            var after = CountCodexSchemaFiles();
            Assert.Equal(before, after);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_SubprocessThrows_StillDeletesSchemaFile()
    {
        var (workspace, promptFile) = NewWorkspace();
        try
        {
            var before = CountCodexSchemaFiles();
            string? observedSchemaPath = null;

            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, remove, name, _) =>
            {
                var schemaIdx = Array.IndexOf(args, "--output-schema");
                observedSchemaPath = args[schemaIdx + 1];
                throw new InvalidOperationException("subprocess exploded");
            };

            var executor = new CodexAgentExecutor(
                TestOptionsMonitor.Create(new CodexCliLlmOptions()),
                NullLogger<CodexAgentExecutor>.Instance,
                runner);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                executor.ExecuteAsync(CreateContext(workspace, promptFile), CancellationToken.None));

            Assert.NotNull(observedSchemaPath);
            Assert.False(File.Exists(observedSchemaPath),
                "Schema file must be deleted when the subprocess throws");

            var after = CountCodexSchemaFiles();
            Assert.Equal(before, after);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutPropagates_StillDeletesSchemaFile()
    {
        var (workspace, promptFile) = NewWorkspace();
        try
        {
            var before = CountCodexSchemaFiles();
            string? observedSchemaPath = null;

            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, remove, name, _) =>
            {
                var schemaIdx = Array.IndexOf(args, "--output-schema");
                observedSchemaPath = args[schemaIdx + 1];
                throw new TimeoutException("fake timeout");
            };

            var executor = new CodexAgentExecutor(
                TestOptionsMonitor.Create(new CodexCliLlmOptions()),
                NullLogger<CodexAgentExecutor>.Instance,
                runner);

            await Assert.ThrowsAsync<TimeoutException>(() =>
                executor.ExecuteAsync(CreateContext(workspace, promptFile), CancellationToken.None));

            Assert.NotNull(observedSchemaPath);
            Assert.False(File.Exists(observedSchemaPath),
                "Schema file must be deleted on timeout");

            var after = CountCodexSchemaFiles();
            Assert.Equal(before, after);
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }
}
