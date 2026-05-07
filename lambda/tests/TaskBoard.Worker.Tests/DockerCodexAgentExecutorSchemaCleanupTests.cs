using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Tests.Helpers;

namespace TaskBoard.Worker.Tests;

/// <summary>
/// Regression tests for the <c>finally</c>-block cleanup of the
/// <see cref="DockerCodexAgentExecutor"/> temp schema file. The schema is
/// written to <c>{TempPath}/docker-codex-schema-{guid}.json</c> before the
/// container starts (mounted RO at <c>/tmp/codex-schema.json</c> inside) and
/// must be deleted on every exit path — including timeout, cancellation, and
/// arbitrary subprocess throws.
/// </summary>
/// <remarks>
/// The temp-file naming uses the <c>docker-codex-schema-</c> prefix
/// (deliberately distinct from host Codex's <c>codex-schema-</c> prefix) so
/// the count assertions in this suite don't pollute or get polluted by
/// <see cref="CodexAgentExecutorSchemaCleanupTests"/> running in parallel.
/// </remarks>
public class DockerCodexAgentExecutorSchemaCleanupTests
{
    private static AgentExecutionContext CreateContext(string workspacePath, string systemPromptFile) =>
        new(
            TargetCardId: "42",
            TargetCardTitle: "Test",
            WorkspacePath: workspacePath,
            TaskPrompt: "do a thing",
            SystemPromptFilePath: systemPromptFile,
            Model: "gpt-5.4-mini");

    private static (string Workspace, string SystemPromptFile) NewWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "docker-codex-schema-cleanup-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var promptFile = Path.Combine(dir, "system.md");
        File.WriteAllText(promptFile, "# System\nTest.");
        return (dir, promptFile);
    }

    private static DockerCodexAgentExecutor CreateExecutor(ProcessRunnerDelegate runner) =>
        new(
            TestOptionsMonitor.Create(new DockerCodexAgentOptions { TimeoutSeconds = 30 }),
            Helpers.TestTenant.Instance,
            NullLogger<DockerCodexAgentExecutor>.Instance,
            mountBuilder: null,
            processRunner: runner);

    [Fact]
    public async Task ExecuteAsync_Success_DeletesSchemaFile()
    {
        var (workspace, promptFile) = NewWorkspace();
        try
        {
            string? observedSchemaPath = null;

            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, remove, name, _) =>
            {
                // The schema file is mounted as a host-path volume:
                // `-v {hostFile}:{containerPath}:ro`. Locate that arg.
                var schemaArg = args.FirstOrDefault(a =>
                    a.Contains("docker-codex-schema-")
                    && a.EndsWith("/tmp/codex-schema.json:ro", StringComparison.OrdinalIgnoreCase));
                Assert.NotNull(schemaArg);
                // Extract host path (everything before the first `:` after the drive letter)
                observedSchemaPath = schemaArg.Replace(":/tmp/codex-schema.json:ro", "");
                Assert.True(File.Exists(observedSchemaPath),
                    "Schema file should exist while the container is running");
                return Task.FromResult((0,
                    """{"type":"turn.completed","structured_output":{"outcome":"COMPLETE","detail":"ok"}}""",
                    ""));
            };

            await CreateExecutor(runner).ExecuteAsync(
                CreateContext(workspace, promptFile), CancellationToken.None);

            // Schema must be gone after success.
            Assert.NotNull(observedSchemaPath);
            Assert.False(File.Exists(observedSchemaPath));
            // Don't assert on the global file count — sibling test classes
            // (DockerCodexAgentExecutorTests, DockerCodexAgentExecutorContractTests,
            // ...) run in parallel by xUnit's default and create their own
            // docker-codex-schema-*.json temp files. The observed-path check
            // above is the reliable signal that THIS test's cleanup ran.
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_TimeoutFromRunner_StillDeletesSchemaFile()
    {
        // Regression guard: when the docker subprocess times out, the
        // executor catches TimeoutException, calls StopAndRemoveContainerAsync
        // (best-effort — may or may not actually find docker on PATH; that's
        // fine, it never throws), and re-throws. The finally block must still
        // delete the schema temp file.
        var (workspace, promptFile) = NewWorkspace();
        try
        {
            string? observedSchemaPath = null;

            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, remove, name, _) =>
            {
                var schemaArg = args.FirstOrDefault(a =>
                    a.Contains("docker-codex-schema-")
                    && a.EndsWith("/tmp/codex-schema.json:ro", StringComparison.OrdinalIgnoreCase));
                if (schemaArg is not null)
                    observedSchemaPath = schemaArg.Replace(":/tmp/codex-schema.json:ro", "");
                throw new TimeoutException("simulated docker run timeout");
            };

            await Assert.ThrowsAsync<TimeoutException>(() =>
                CreateExecutor(runner).ExecuteAsync(
                    CreateContext(workspace, promptFile), CancellationToken.None));

            Assert.NotNull(observedSchemaPath);
            Assert.False(File.Exists(observedSchemaPath),
                "Schema file must be deleted on TimeoutException — the cleanup path did not fire");
            // Don't assert on the global file count — sibling test classes
            // (DockerCodexAgentExecutorTests, DockerCodexAgentExecutorContractTests,
            // ...) run in parallel by xUnit's default and create their own
            // docker-codex-schema-*.json temp files. The observed-path check
            // above is the reliable signal that THIS test's cleanup ran.
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_OperationCanceledFromRunner_StillDeletesSchemaFile()
    {
        var (workspace, promptFile) = NewWorkspace();
        try
        {
            string? observedSchemaPath = null;

            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, remove, name, _) =>
            {
                var schemaArg = args.FirstOrDefault(a =>
                    a.Contains("docker-codex-schema-")
                    && a.EndsWith("/tmp/codex-schema.json:ro", StringComparison.OrdinalIgnoreCase));
                if (schemaArg is not null)
                    observedSchemaPath = schemaArg.Replace(":/tmp/codex-schema.json:ro", "");
                throw new OperationCanceledException("simulated cancellation");
            };

            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                CreateExecutor(runner).ExecuteAsync(
                    CreateContext(workspace, promptFile), CancellationToken.None));

            Assert.NotNull(observedSchemaPath);
            Assert.False(File.Exists(observedSchemaPath));
            // Don't assert on the global file count — sibling test classes
            // (DockerCodexAgentExecutorTests, DockerCodexAgentExecutorContractTests,
            // ...) run in parallel by xUnit's default and create their own
            // docker-codex-schema-*.json temp files. The observed-path check
            // above is the reliable signal that THIS test's cleanup ran.
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_ArbitraryThrowFromRunner_StillDeletesSchemaFile()
    {
        // Generic exception path (not Timeout/Cancel) — the executor logs
        // repro info and re-throws. Cleanup still must run.
        var (workspace, promptFile) = NewWorkspace();
        try
        {
            string? observedSchemaPath = null;

            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, remove, name, _) =>
            {
                var schemaArg = args.FirstOrDefault(a =>
                    a.Contains("docker-codex-schema-")
                    && a.EndsWith("/tmp/codex-schema.json:ro", StringComparison.OrdinalIgnoreCase));
                if (schemaArg is not null)
                    observedSchemaPath = schemaArg.Replace(":/tmp/codex-schema.json:ro", "");
                throw new InvalidOperationException("subprocess exploded");
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateExecutor(runner).ExecuteAsync(
                    CreateContext(workspace, promptFile), CancellationToken.None));

            Assert.NotNull(observedSchemaPath);
            Assert.False(File.Exists(observedSchemaPath));
            // Don't assert on the global file count — sibling test classes
            // (DockerCodexAgentExecutorTests, DockerCodexAgentExecutorContractTests,
            // ...) run in parallel by xUnit's default and create their own
            // docker-codex-schema-*.json temp files. The observed-path check
            // above is the reliable signal that THIS test's cleanup ran.
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExit_DeletesSchemaFile()
    {
        // Non-zero exit path — executor logs error + throws InvalidOperationException
        // (or CliInfrastructureException for infra exit codes). Cleanup must run.
        var (workspace, promptFile) = NewWorkspace();
        try
        {
            string? observedSchemaPath = null;

            ProcessRunnerDelegate runner = (exe, args, wd, t, ct, stdin, remove, name, _) =>
            {
                var schemaArg = args.FirstOrDefault(a =>
                    a.Contains("docker-codex-schema-")
                    && a.EndsWith("/tmp/codex-schema.json:ro", StringComparison.OrdinalIgnoreCase));
                if (schemaArg is not null)
                    observedSchemaPath = schemaArg.Replace(":/tmp/codex-schema.json:ro", "");
                return Task.FromResult((1, "garbage stdout", "some stderr"));
            };

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                CreateExecutor(runner).ExecuteAsync(
                    CreateContext(workspace, promptFile), CancellationToken.None));

            Assert.NotNull(observedSchemaPath);
            Assert.False(File.Exists(observedSchemaPath),
                "Schema file must be deleted on non-zero exit");
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task StopAndRemoveContainerAsync_DockerNotInstalled_NeverThrows()
    {
        // The cleanup path uses `ProcessRunner.RunProcessAsync` directly (not
        // the injected delegate), so on a machine without docker on PATH the
        // calls fail. The method must swallow every exception — anything else
        // would mask the original timeout/cancel exception that triggered the
        // cleanup in the first place.
        var executor = CreateExecutor(
            (exe, args, wd, t, ct, stdin, remove, name, _) =>
                Task.FromResult((0, "", "")));

        // Running this in a test environment where `docker` may or may not be
        // installed; either way it must not throw to the caller.
        var ex = await Record.ExceptionAsync(
            () => executor.StopAndRemoveContainerAsync("aiboard-cdx-nonexistent-test"));
        Assert.Null(ex);
    }
}
