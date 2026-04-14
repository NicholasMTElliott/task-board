using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

public class DockerAgentExecutorTests
{
    private static DockerAgentExecutor CreateExecutor(
        string imageName = "aiboard-sandbox:latest",
        string promptMountPoint = "/mnt/aiboard/prompts",
        decimal maxBudgetUsd = 10.00m,
        Dictionary<string, DockerMount>? additionalMounts = null)
    {
        var opts = Options.Create(new DockerAgentOptions
        {
            ImageName = imageName,
            PromptMountPoint = promptMountPoint,
            MaxBudgetUsd = maxBudgetUsd,
            ContainerNamePrefix = "aiboard-run",
            AdditionalMounts = additionalMounts ?? [],
        });
        return new DockerAgentExecutor(opts, TaskBoard.Worker.Tests.Helpers.TestTenant.Instance,
            NullLogger<DockerAgentExecutor>.Instance);
    }

    private static AgentExecutionContext CreateContext(
        string model = "claude-sonnet-4-6",
        string systemPromptFilePath = "/app/prompts/senior_engineer.md",
        IReadOnlyDictionary<string, string>? providerParams = null)
    {
        return new AgentExecutionContext(
            TargetCardId: "42",
            TargetCardTitle: "Test Feature",
            WorkspacePath: "/tmp/workspace",
            TaskPrompt: "implement the feature",
            SystemPromptFilePath: systemPromptFilePath,
            Model: model,
            ProviderParams: providerParams);
    }

    // ── BuildClaudeArgumentList ──────────────────────────────────────────────

    [Fact]
    public void BuildClaudeArgumentList_ContainsRequiredFlags()
    {
        var executor = CreateExecutor();
        var args = executor.BuildClaudeArgumentList(
            CreateContext(model: "claude-opus-4-6"),
            "/mnt/aiboard/prompts/senior_engineer.md");

        Assert.Contains("--model", args);
        Assert.Contains("claude-opus-4-6", args);
        Assert.Contains("--output-format", args);
        Assert.Contains("stream-json", args);
        Assert.Contains("--verbose", args);
        Assert.Contains("--json-schema", args);
        Assert.Contains("--no-session-persistence", args);
        Assert.Contains("--permission-mode", args);
        Assert.Contains("bypassPermissions", args);
        Assert.Contains("--allowedTools", args);
        Assert.Contains("*", args);
    }

    [Fact]
    public void BuildClaudeArgumentList_SystemPromptUsesContainerPath()
    {
        var executor = CreateExecutor();
        var containerPath = "/mnt/aiboard/prompts/senior_engineer.md";
        var args = executor.BuildClaudeArgumentList(CreateContext(), containerPath);

        var idx = Array.IndexOf(args, "--append-system-prompt-file");
        Assert.True(idx >= 0, "Expected --append-system-prompt-file flag");
        Assert.Equal(containerPath, args[idx + 1]);
    }

    [Fact]
    public void BuildClaudeArgumentList_PrintIsLastFlag()
    {
        var executor = CreateExecutor();
        var args = executor.BuildClaudeArgumentList(
            CreateContext(), "/mnt/prompts/sp.md");

        Assert.Equal("--print", args[^1]);
    }

    [Fact]
    public void BuildClaudeArgumentList_WithEffortParam_IncludesEffortFlag()
    {
        var executor = CreateExecutor();
        var context = CreateContext(providerParams: new Dictionary<string, string>
        {
            ["effort"] = "max"
        });
        var args = executor.BuildClaudeArgumentList(context, "/mnt/prompts/sp.md");

        var idx = Array.IndexOf(args, "--effort");
        Assert.True(idx >= 0, "Expected --effort flag");
        Assert.Equal("max", args[idx + 1]);
    }

    [Fact]
    public void BuildClaudeArgumentList_WithoutEffortParam_NoEffortFlag()
    {
        var executor = CreateExecutor();
        var args = executor.BuildClaudeArgumentList(CreateContext(), "/mnt/prompts/sp.md");

        Assert.DoesNotContain("--effort", args);
    }

    [Fact]
    public void BuildClaudeArgumentList_PermissionModeNone_OmitsPermissionFlags()
    {
        var executor = CreateExecutor();
        var context = CreateContext(providerParams: new Dictionary<string, string>
        {
            ["permissionMode"] = "none"
        });
        var args = executor.BuildClaudeArgumentList(context, "/mnt/prompts/sp.md");

        Assert.DoesNotContain("--permission-mode", args);
        Assert.DoesNotContain("bypassPermissions", args);
        Assert.DoesNotContain("--allowedTools", args);
        // Required flags still present
        Assert.Contains("--model", args);
        Assert.Contains("--output-format", args);
        Assert.Contains("--json-schema", args);
        Assert.Contains("--no-session-persistence", args);
    }

    [Fact]
    public void BuildClaudeArgumentList_MaxBudgetOverride_UsesProviderParam()
    {
        var executor = CreateExecutor(maxBudgetUsd: 5.00m);
        var context = CreateContext(providerParams: new Dictionary<string, string>
        {
            ["maxBudget"] = "0.25"
        });
        var args = executor.BuildClaudeArgumentList(context, "/mnt/prompts/sp.md");

        var idx = Array.IndexOf(args, "--max-budget-usd");
        Assert.True(idx >= 0, "Expected --max-budget-usd flag");
        Assert.Equal("0.25", args[idx + 1]);
    }

    [Fact]
    public void BuildClaudeArgumentList_NoMaxBudgetOverride_UsesOptionDefault()
    {
        var executor = CreateExecutor(maxBudgetUsd: 3.50m);
        var args = executor.BuildClaudeArgumentList(CreateContext(), "/mnt/prompts/sp.md");

        var idx = Array.IndexOf(args, "--max-budget-usd");
        Assert.True(idx >= 0, "Expected --max-budget-usd flag");
        Assert.Equal("3.50", args[idx + 1]);
    }

    // ── BuildDockerArgumentList ──────────────────────────────────────────────

    [Fact]
    public void BuildDockerArgumentList_ContainsCoreDockerFlags()
    {
        var executor = CreateExecutor(imageName: "my-sandbox:v1");
        var dockerArgs = executor.BuildDockerArgumentList(
            "aiboard-run-42-abc", "/host/prompts", ["--model", "sonnet", "--print"]);

        Assert.Contains("run", dockerArgs);
        Assert.Contains("--rm", dockerArgs);
        Assert.Contains("-i", dockerArgs);
        Assert.Contains("--name", dockerArgs);
        Assert.Contains("aiboard-run-42-abc", dockerArgs);
    }

    [Fact]
    public void BuildDockerArgumentList_ContainsImageName()
    {
        var executor = CreateExecutor(imageName: "aiboard-sandbox:latest");
        var dockerArgs = executor.BuildDockerArgumentList(
            "container1", "/host/prompts", ["--print"]);

        Assert.Contains("aiboard-sandbox:latest", dockerArgs);
    }

    [Fact]
    public void BuildDockerArgumentList_ContainsClaudeExecutable()
    {
        var executor = CreateExecutor();
        var dockerArgs = executor.BuildDockerArgumentList(
            "container1", "/host/prompts", ["--print"]);

        Assert.Contains("claude", dockerArgs);
    }

    [Fact]
    public void BuildDockerArgumentList_MountsSystemPromptDirectory()
    {
        var executor = CreateExecutor(promptMountPoint: "/mnt/aiboard/prompts");
        var dockerArgs = executor.BuildDockerArgumentList(
            "container1", "/host/prompts", ["--print"]);

        // Find -v flag and check the mount spec
        var vIdx = Array.IndexOf(dockerArgs, "-v");
        Assert.True(vIdx >= 0, "Expected -v flag for system prompt mount");
        Assert.Equal("/host/prompts:/mnt/aiboard/prompts:ro", dockerArgs[vIdx + 1]);
    }

    [Fact]
    public void BuildDockerArgumentList_EmptyHostPromptDir_SkipsPromptMount()
    {
        var executor = CreateExecutor();
        var dockerArgs = executor.BuildDockerArgumentList(
            "container1", "", ["--print"]);

        // No -v flag should be present for an empty host dir
        Assert.DoesNotContain("-v", dockerArgs);
    }

    [Fact]
    public void BuildDockerArgumentList_AdditionalMounts_IncludesThemAfterPromptMount()
    {
        var mounts = new Dictionary<string, DockerMount>
        {
            ["workspace"] = new DockerMount
            {
                HostPath = "/host/workspace",
                ContainerPath = "/workspace",
                ReadOnly = false,
            }
        };
        var executor = CreateExecutor(additionalMounts: mounts);
        var dockerArgs = executor.BuildDockerArgumentList(
            "container1", "/host/prompts", ["--print"]);

        // Both -v entries present
        var vIndices = dockerArgs
            .Select((a, i) => (arg: a, idx: i))
            .Where(x => x.arg == "-v")
            .Select(x => x.idx)
            .ToList();

        Assert.Equal(2, vIndices.Count);
        // First mount: system prompt (read-only)
        Assert.Contains(":ro", dockerArgs[vIndices[0] + 1]);
        // Second mount: workspace (not read-only)
        Assert.DoesNotContain(":ro", dockerArgs[vIndices[1] + 1]);
        Assert.Contains("/host/workspace:/workspace", dockerArgs[vIndices[1] + 1]);
    }

    [Fact]
    public void BuildDockerArgumentList_AdditionalReadOnlyMount_HasRoSuffix()
    {
        var mounts = new Dictionary<string, DockerMount>
        {
            ["creds"] = new DockerMount
            {
                HostPath = "/host/creds",
                ContainerPath = "/run/creds",
                ReadOnly = true,
            }
        };
        var executor = CreateExecutor(additionalMounts: mounts);
        var dockerArgs = executor.BuildDockerArgumentList(
            "container1", "", ["--print"]);

        var vIdx = Array.IndexOf(dockerArgs, "-v");
        Assert.True(vIdx >= 0, "Expected -v flag");
        Assert.Equal("/host/creds:/run/creds:ro", dockerArgs[vIdx + 1]);
    }

    [Fact]
    public void BuildDockerArgumentList_ClaudeArgsAppendedAfterImage()
    {
        var executor = CreateExecutor(imageName: "sandbox:test");
        var claudeArgs = new[] { "--model", "claude-sonnet-4-6", "--print" };
        var dockerArgs = executor.BuildDockerArgumentList("c1", "/prompts", claudeArgs);

        var imageIdx = Array.IndexOf(dockerArgs, "sandbox:test");
        var claudeIdx = Array.IndexOf(dockerArgs, "claude");
        var modelIdx = Array.IndexOf(dockerArgs, "--model");

        Assert.True(imageIdx >= 0, "Image should be present");
        Assert.True(claudeIdx > imageIdx, "claude executable should come after image");
        Assert.True(modelIdx > claudeIdx, "claude args should come after claude executable");
    }

    // ── TranslateSystemPromptPath ─────────────────────────────────────────────

    [Fact]
    public void TranslateSystemPromptPath_ReturnsCorrectContainerPath()
    {
        // Use OS-native path so Path.GetDirectoryName behaves correctly on Windows and Linux
        var hostPromptsDir = Path.Combine(Path.GetTempPath(), "aiboard", "prompts");
        var hostFile = Path.Combine(hostPromptsDir, "senior_engineer.md");

        var executor = CreateExecutor(promptMountPoint: "/mnt/aiboard/prompts");
        var (hostDir, containerPath) = executor.TranslateSystemPromptPath(hostFile);

        Assert.Equal(hostPromptsDir, hostDir);
        Assert.Equal("/mnt/aiboard/prompts/senior_engineer.md", containerPath);
    }

    [Fact]
    public void TranslateSystemPromptPath_TrailingSlashOnMountPoint_NoDoubleSlash()
    {
        var hostFile = Path.Combine(Path.GetTempPath(), "gate_checker.md");
        var executor = CreateExecutor(promptMountPoint: "/mnt/aiboard/prompts/");
        var (_, containerPath) = executor.TranslateSystemPromptPath(hostFile);

        Assert.Equal("/mnt/aiboard/prompts/gate_checker.md", containerPath);
        Assert.DoesNotContain("//", containerPath);
    }

    [Fact]
    public void TranslateSystemPromptPath_FilenameOnlyPath_ReturnsEmptyHostDir()
    {
        var executor = CreateExecutor(promptMountPoint: "/mnt/prompts");
        var (hostDir, containerPath) = executor.TranslateSystemPromptPath("system.md");

        Assert.Equal("", hostDir);
        Assert.Equal("/mnt/prompts/system.md", containerPath);
    }

    // ── IsDockerExitCode ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(125)] // Docker daemon error
    [InlineData(126)] // Permission denied
    [InlineData(127)] // Command not found
    [InlineData(137)] // Container killed (OOM / SIGKILL)
    public void IsDockerExitCode_DockerSpecificCodes_ReturnsTrue(int exitCode)
    {
        Assert.True(DockerAgentExecutor.IsDockerExitCode(exitCode));
    }

    [Theory]
    [InlineData(0)]   // Success
    [InlineData(1)]   // Generic Claude CLI error
    [InlineData(2)]   // Argument parse error
    [InlineData(124)] // Highest non-Docker code
    public void IsDockerExitCode_NonDockerCodes_ReturnsFalse(int exitCode)
    {
        Assert.False(DockerAgentExecutor.IsDockerExitCode(exitCode));
    }

    // ── BuildContainerName ───────────────────────────────────────────────────

    [Fact]
    public void BuildContainerName_ContainsPrefix()
    {
        var executor = CreateExecutor();
        var name = executor.BuildContainerName("42");

        // Format: {prefix}-{tenantHash}-{cardId}-{random8}
        Assert.StartsWith("aiboard-run-", name);
        Assert.Contains("-42-", name);
    }

    [Fact]
    public void BuildContainerName_IsUnique()
    {
        var executor = CreateExecutor();
        var name1 = executor.BuildContainerName("42");
        var name2 = executor.BuildContainerName("42");

        Assert.NotEqual(name1, name2);
    }

    [Fact]
    public void BuildContainerName_ContainsCardId()
    {
        var executor = CreateExecutor();
        var name = executor.BuildContainerName("card-99");

        Assert.Contains("card-99", name);
    }

    // ── ParseStreamOutput delegation ─────────────────────────────────────────
    // ParseStreamOutput is shared via AgentOutputParser; verify DockerAgentExecutor
    // produces correct results via the same parser path.

    [Fact]
    public void AgentOutputParser_ParseStreamOutput_ExtractsStructuredResult()
    {
        var stdout = string.Join("\n",
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Working on it."}]}}""",
            """{"type":"result","structured_output":{"outcome":"COMPLETE","detail":"Done"}}"""
        );

        var (resultJson, log) = AgentOutputParser.ParseStreamOutput(stdout,
            NullLogger.Instance);

        Assert.NotNull(resultJson);
        Assert.Contains("structured_output", resultJson);
        Assert.Contains("Working on it.", log);
    }

    [Fact]
    public void AgentOutputParser_ParseStreamOutput_NeedsInfoOutcome()
    {
        var stdout =
            """{"type":"result","structured_output":{"outcome":"NEEDS_INFO","detail":"Need clarification"}}""";

        var (resultJson, _) = AgentOutputParser.ParseStreamOutput(stdout,
            NullLogger.Instance);

        Assert.NotNull(resultJson);
        var parsed = AgentOutputParser.ParseResult(resultJson);
        Assert.Equal(AgentOutcome.NEEDS_INFO, parsed.Outcome);
        Assert.Equal("Need clarification", parsed.Detail);
    }

    [Fact]
    public void AgentOutputParser_ParseStreamOutput_MalformedLinesSkipped()
    {
        var stdout = string.Join("\n",
            "not json",
            """{"type":"result","structured_output":{"outcome":"COMPLETE"}}"""
        );

        var (resultJson, _) = AgentOutputParser.ParseStreamOutput(stdout,
            NullLogger.Instance);

        Assert.NotNull(resultJson);
    }

    // ── Integration: Docker infrastructure ───────────────────────────────────

    /// <summary>
    /// Verifies that ProcessRunner can pipe stdin through <c>docker run -i</c>.
    /// Skipped automatically when Docker is not available.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task DockerInfrastructure_PipesStdinThroughContainer()
    {
        if (!await IsDockerAvailableAsync())
            return; // Skip when Docker not available

        const string expectedPayload = "hello-docker-pipe";

        // docker run --rm -i alpine cat — echoes stdin back to stdout
        var (exitCode, stdout, _) = await ProcessRunner.RunProcessAsync(
            "docker",
            ["run", "--rm", "-i", "alpine", "cat"],
            workingDirectory: Path.GetTempPath(),
            timeoutSeconds: 20,
            cancellationToken: CancellationToken.None,
            stdinData: expectedPayload,
            agentName: "Docker pipe test");

        Assert.Equal(0, exitCode);
        Assert.Contains(expectedPayload, stdout);
    }

    // ── StopGracePeriod constants ─────────────────────────────────────────────

    [Fact]
    public void StopCleanup_Constants_TimeoutExceedsGracePeriod()
    {
        // ProcessRunner timeout for docker stop must be > grace period so docker stop
        // has time to complete its full SIGTERM window before being killed.
        Assert.True(
            DockerAgentExecutor.StopCommandTimeoutSeconds > DockerAgentExecutor.StopGracePeriodSeconds,
            "StopCommandTimeoutSeconds must exceed StopGracePeriodSeconds");
        Assert.Equal(30, DockerAgentExecutor.StopGracePeriodSeconds);
        Assert.True(DockerAgentExecutor.RemoveCommandTimeoutSeconds > 0,
            "RemoveCommandTimeoutSeconds must be positive");
    }

    // ── Integration: StopAndRemoveContainerAsync ──────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DockerAgentExecutor.StopAndRemoveContainerAsync"/> stops
    /// a running container without throwing.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task StopAndRemoveContainerAsync_RunningContainer_ContainerStops()
    {
        if (!await IsDockerAvailableAsync())
            return;

        var executor = CreateExecutor();
        var containerName = $"aiboard-test-stop-{Guid.NewGuid():N}"[..30];

        // Start a long-running container without --rm so we control its lifecycle
        await StartDetachedContainerAsync(containerName, "alpine", ["sleep", "60"]);

        try
        {
            Assert.True(await IsContainerRunningAsync(containerName),
                "Container should be running before cleanup");

            // Act: cleanup should stop the container without throwing
            await executor.StopAndRemoveContainerAsync(containerName);

            // Assert: container is no longer running
            Assert.False(await IsContainerRunningAsync(containerName),
                "Container should not be running after StopAndRemoveContainerAsync");
        }
        finally
        {
            await RemoveContainerAsync(containerName);
        }
    }

    /// <summary>
    /// Verifies that <see cref="DockerAgentExecutor.StopAndRemoveContainerAsync"/> does
    /// not throw when the container does not exist.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task StopAndRemoveContainerAsync_NonExistentContainer_DoesNotThrow()
    {
        if (!await IsDockerAvailableAsync())
            return;

        var executor = CreateExecutor();

        // Should not throw for a container that was never created
        var ex = await Record.ExceptionAsync(() =>
            executor.StopAndRemoveContainerAsync("aiboard-test-nonexistent-0000000000"));

        Assert.Null(ex);
    }

    /// <summary>
    /// Verifies that <see cref="DockerAgentExecutor.StopAndRemoveContainerAsync"/> does
    /// not throw when the container is already stopped.
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task StopAndRemoveContainerAsync_AlreadyStoppedContainer_DoesNotThrow()
    {
        if (!await IsDockerAvailableAsync())
            return;

        var executor = CreateExecutor();
        var containerName = $"aiboard-test-stopped-{Guid.NewGuid():N}"[..32];

        // Create a container that exits immediately (no --rm, so it stays as stopped)
        await RunContainerToCompletionAsync(containerName, "alpine", ["true"]);

        try
        {
            var ex = await Record.ExceptionAsync(() =>
                executor.StopAndRemoveContainerAsync(containerName));

            Assert.Null(ex);
        }
        finally
        {
            await RemoveContainerAsync(containerName);
        }
    }

    // ── Docker test helpers ───────────────────────────────────────────────────

    private static async Task StartDetachedContainerAsync(
        string containerName, string image, string[] command)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "docker",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "run", "-d", "--name", containerName, image }.Concat(command))
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        await process.WaitForExitAsync();
    }

    private static async Task RunContainerToCompletionAsync(
        string containerName, string image, string[] command)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "docker",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "run", "--name", containerName, image }.Concat(command))
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        await process.WaitForExitAsync();
    }

    private static async Task<bool> IsContainerRunningAsync(string containerName)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "docker",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "ps", "--filter", $"name=^/{containerName}$", "--format", "{{.Names}}" })
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return output.Trim() == containerName;
    }

    private static async Task RemoveContainerAsync(string containerName)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "docker",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "rm", "-f", containerName })
            process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        await process.WaitForExitAsync();
    }

    private static async Task<bool> IsDockerAvailableAsync()
    {
        try
        {
            using var process = new System.Diagnostics.Process();
            process.StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "docker",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("info");
            process.Start();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await process.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(); } catch { }
                return false;
            }

            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
