using System.Text;
using Microsoft.Extensions.Options;

namespace TaskBoard.Worker.Clients;

/// <summary>
/// Executes the Claude CLI inside a Docker container via <c>docker run</c>.
/// Reuses <see cref="AgentOutputParser"/> for NDJSON stream parsing and
/// <see cref="ClaudeAgentExecutor.IsRateLimited"/> for rate-limit detection.
/// </summary>
public sealed class DockerClaudeAgentExecutor(
    IOptions<DockerClaudeAgentOptions> options,
    ITenantIdentifier tenant,
    ILogger<DockerClaudeAgentExecutor> logger,
    DockerClaudeMountBuilder? mountBuilder = null,
    ProcessRunnerDelegate? processRunner = null) : IAgentExecutor
{
    private readonly DockerClaudeAgentOptions _options = options.Value;
    private readonly ProcessRunnerDelegate _runProcess = processRunner ?? ProcessRunner.RunProcessAsync;

    private const string DockerExecutable = "docker";

    // Claude CLI is installed at this path inside the aiboard-sandbox image.
    private const string ContainerClaudeExecutable = "claude";

    // Grace period passed to `docker stop -t` (seconds before SIGKILL is sent).
    internal const int StopGracePeriodSeconds = 30;

    // ProcessRunner timeout for the `docker stop` command itself — must exceed
    // StopGracePeriodSeconds so the docker CLI has time to complete its grace period.
    internal const int StopCommandTimeoutSeconds = StopGracePeriodSeconds + 5;

    // ProcessRunner timeout for the `docker rm -f` fallback command.
    internal const int RemoveCommandTimeoutSeconds = 10;

    public async Task<AgentResult> ExecuteAsync(
        AgentExecutionContext context, CancellationToken cancellationToken)
    {
        var containerName = BuildContainerName(context.TargetCardId);

        logger.LogInformation(
            "Launching Docker agent for card {CardId} in {Workspace}, " +
            "model={Model}, image={Image}, container={Container}",
            context.TargetCardId, context.WorkspacePath,
            context.Model, _options.ImageName, containerName);

        // Build workspace/credential mount context if a mount builder is available
        DockerMountContext? mountContext = null;
        if (mountBuilder is not null)
        {
            try
            {
                mountContext = await mountBuilder.BuildAsync(
                    context.WorkspacePath, _options, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to build mount context for card {CardId} — proceeding without workspace mounts",
                    context.TargetCardId);
            }
        }

        try
        {
            // Translate task file path to container-side path when a workspace is mounted
            var taskFilePath = Processing.TaskFileManager.GetTaskFilePath(
                context.WorkspacePath, context.TargetCardId, context.TargetCardTitle);
            var containerTaskFilePath = mountContext?.TranslatePath(taskFilePath) ?? taskFilePath;

            // Mirror task-file translation for the comments file: PromptBuilder
            // embeds context.CommentsFilePath verbatim into the user prompt, but
            // the host path is meaningless inside a Linux container — without
            // translation the agent tries to read a Windows path like
            // C:\…\.aiboard\tasks\…-comments.md, fails, falls back to glob, and
            // burns the timeout window.
            var promptContext = (mountContext is not null && context.CommentsFilePath is not null)
                ? context with { CommentsFilePath = mountContext.TranslatePath(context.CommentsFilePath) }
                : context;
            var userPrompt = ClaudeAgentExecutor.BuildUserPrompt(promptContext, containerTaskFilePath);

            // Translate system prompt file path: mount host directory into container
            var (hostPromptDir, containerPromptPath) = TranslateSystemPromptPath(
                context.SystemPromptFilePath);

            var claudeArgs = BuildClaudeArgumentList(context, containerPromptPath);
            var dockerArgs = BuildDockerArgumentList(
                containerName, hostPromptDir, claudeArgs, mountContext);

            logger.LogDebug("Docker command: {Executable} {Args}",
                DockerExecutable, ProcessRunner.FormatArgsForLogging(dockerArgs));

            int exitCode;
            string stdout, stderr;
            try
            {
                (exitCode, stdout, stderr) = await _runProcess(
                    DockerExecutable, dockerArgs, context.WorkspacePath,
                    _options.TimeoutSeconds, cancellationToken,
                    stdinData: userPrompt,
                    envVarsToRemove: new[] { "CLAUDECODE" },
                    agentName: $"Docker agent ({containerName})",
                    inactivityTimeoutSeconds: _options.InactivityTimeoutSeconds);
            }
            catch (TimeoutException)
            {
                await StopAndRemoveContainerAsync(containerName);
                AgentOutputParser.LogReproductionInfo(
                    logger, "Docker/Claude", DockerExecutable, dockerArgs,
                    userPrompt, context.WorkspacePath);
                throw;
            }
            catch (OperationCanceledException)
            {
                await StopAndRemoveContainerAsync(containerName);
                throw;
            }
            catch (Exception)
            {
                AgentOutputParser.LogReproductionInfo(
                    logger, "Docker/Claude", DockerExecutable, dockerArgs,
                    userPrompt, context.WorkspacePath);
                throw;
            }

            if (exitCode != 0)
            {
                // Check for rate limiting from Claude CLI stderr before classifying as Docker error
                if (!IsDockerExitCode(exitCode) && ClaudeAgentExecutor.IsRateLimited(stderr))
                {
                    var snippet = stderr[..Math.Min(500, stderr.Length)].Trim();
                    logger.LogWarning(
                        "Docker/Claude rate limited for card {CardId}. Exit code {ExitCode}. Stderr: {Stderr}",
                        context.TargetCardId, exitCode, snippet);
                    throw new RateLimitException(
                        $"Docker/Claude rate limited (exit code {exitCode}). Stderr: {snippet}",
                        RateLimitSource.AgentCli);
                }

                // Stream-json mode emits the machine-readable rate-limit signal in stdout
                // (a `rate_limit_event` NDJSON line), not stderr. Without this branch the
                // 5-hour-window-rejected / out-of-credits shape gets misclassified as
                // AGENT_ERROR and the card moves to Problems instead of being held.
                if (!IsDockerExitCode(exitCode) && ClaudeAgentExecutor.IsRateLimitedStdout(stdout))
                {
                    logger.LogWarning(
                        "Docker/Claude rate limited for card {CardId} via stdout rate_limit_event. Exit code {ExitCode}.",
                        context.TargetCardId, exitCode);
                    throw new RateLimitException(
                        $"Docker/Claude rate limited (exit code {exitCode}, rate_limit_event in stdout).",
                        RateLimitSource.AgentCli);
                }

                logger.LogError(
                    "Docker agent exited with code {ExitCode}. Stderr: {Stderr}. Stdout: {Stdout}",
                    exitCode, stderr, stdout[..Math.Min(500, stdout.Length)]);

                AgentOutputParser.LogReproductionInfo(
                    logger, "Docker/Claude", DockerExecutable, dockerArgs,
                    userPrompt, context.WorkspacePath);

                var stderrSnippet = stderr[..Math.Min(1000, stderr.Length)].Trim();
                var stdoutSnippet = stdout[..Math.Min(500, stdout.Length)].Trim();

                var exitSource = IsDockerExitCode(exitCode) ? "Docker" : "Claude CLI";
                var detail = new StringBuilder($"{exitSource} exited with code {exitCode}.");
                if (!string.IsNullOrEmpty(stderrSnippet))
                    detail.Append($"\nStderr: {stderrSnippet}");
                if (!string.IsNullOrEmpty(stdoutSnippet))
                    detail.Append($"\nStdout: {stdoutSnippet}");

                // Docker daemon-level failures (125/126/127/137) and Claude-CLI
                // shell-level failures (126/127) are infrastructure problems.
                if (IsDockerExitCode(exitCode)
                    || ClaudeAgentExecutor.IsInfrastructureExitCode(exitCode))
                    throw new CliInfrastructureException(detail.ToString());

                throw new InvalidOperationException(detail.ToString());
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                if (ClaudeAgentExecutor.IsRateLimited(stderr))
                {
                    var snippet = stderr[..Math.Min(500, stderr.Length)].Trim();
                    logger.LogWarning(
                        "Docker/Claude rate limited for card {CardId} (exit code 0, empty output). Stderr: {Stderr}",
                        context.TargetCardId, snippet);
                    throw new RateLimitException(
                        $"Docker/Claude rate limited (exit code 0, empty output). Stderr: {snippet}",
                        RateLimitSource.AgentCli);
                }

                logger.LogError("Docker agent returned empty output. Stderr: {Stderr}", stderr);
                AgentOutputParser.LogReproductionInfo(
                    logger, "Docker/Claude", DockerExecutable, dockerArgs,
                    userPrompt, context.WorkspacePath);
                throw new InvalidOperationException(
                    $"Docker/Claude returned empty output. Stderr: {stderr[..Math.Min(500, stderr.Length)].Trim()}");
            }

            logger.LogDebug("Docker agent raw stdout ({Length} chars):\n{Stdout}",
                stdout.Length, stdout[..Math.Min(10000, stdout.Length)]);

            var (resultJson, conversationLog) = AgentOutputParser.ParseStreamOutput(stdout, logger);

            if (!string.IsNullOrEmpty(conversationLog))
            {
                logger.LogInformation("Docker agent conversation ({Length} chars):\n{Log}",
                    conversationLog.Length, conversationLog[..Math.Min(5000, conversationLog.Length)]);
            }

            var result = resultJson is not null
                ? AgentOutputParser.ParseResult(resultJson)
                : AgentOutputParser.ParseResult(stdout);

            var resultWithLog = result with { ConversationLog = conversationLog };
            logger.LogInformation(
                "Docker agent complete, outcome={Outcome}, detail={Detail}, questions={QuestionCount}",
                resultWithLog.Outcome, resultWithLog.Detail ?? "(none)", resultWithLog.Questions?.Count ?? 0);
            return resultWithLog;
        }
        finally
        {
            if (mountContext is not null)
                await mountContext.DisposeAsync();
        }
    }

    /// <summary>
    /// Best-effort container cleanup: issues <c>docker stop -t {StopGracePeriodSeconds}</c>
    /// and, if that fails, falls back to <c>docker rm -f</c>. Uses
    /// <see cref="CancellationToken.None"/> because the caller's token may already be
    /// cancelled. Never throws — failures are logged as warnings.
    /// </summary>
    internal async Task StopAndRemoveContainerAsync(string containerName)
    {
        logger.LogWarning(
            "Issuing explicit docker stop for container {ContainerName} after timeout/cancellation",
            containerName);
        try
        {
            int stopExitCode;
            try
            {
                (stopExitCode, _, _) = await ProcessRunner.RunProcessAsync(
                    DockerExecutable,
                    ["stop", "-t", StopGracePeriodSeconds.ToString(), containerName],
                    Path.GetTempPath(),
                    StopCommandTimeoutSeconds,
                    CancellationToken.None,
                    agentName: $"docker stop {containerName}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "docker stop for container {ContainerName} threw an exception; proceeding to docker rm -f",
                    containerName);
                stopExitCode = -1;
            }

            if (stopExitCode == 0)
            {
                // Container stopped cleanly; --rm will remove it automatically on exit.
                logger.LogWarning(
                    "Container {ContainerName} stopped successfully", containerName);
                return;
            }

            logger.LogWarning(
                "docker stop for container {ContainerName} exited {ExitCode}; issuing docker rm -f",
                containerName, stopExitCode);

            int rmExitCode;
            try
            {
                (rmExitCode, _, _) = await ProcessRunner.RunProcessAsync(
                    DockerExecutable,
                    ["rm", "-f", containerName],
                    Path.GetTempPath(),
                    RemoveCommandTimeoutSeconds,
                    CancellationToken.None,
                    agentName: $"docker rm -f {containerName}");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "docker rm -f for container {ContainerName} threw an exception; container may be orphaned",
                    containerName);
                return;
            }

            if (rmExitCode == 0)
                logger.LogWarning("Container {ContainerName} removed via docker rm -f", containerName);
            else
                logger.LogWarning(
                    "docker rm -f for container {ContainerName} exited {ExitCode}; container may be orphaned",
                    containerName, rmExitCode);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Unexpected error during cleanup for container {ContainerName}; container may be orphaned",
                containerName);
        }
    }

    /// <summary>
    /// Returns true for exit codes produced by the Docker daemon itself (not Claude CLI):
    /// <list type="bullet">
    ///   <item>125 — Docker daemon error (cannot create/start container)</item>
    ///   <item>126 — Container command cannot be invoked (permission denied)</item>
    ///   <item>127 — Container command not found (claude not in image)</item>
    ///   <item>137 — Container killed (OOM, SIGKILL, or timeout kill)</item>
    /// </list>
    /// </summary>
    internal static bool IsDockerExitCode(int exitCode) =>
        exitCode is 125 or 126 or 127 or 137;

    /// <summary>
    /// Translates the host-side system prompt file path into a container-side path
    /// by mapping its parent directory to <see cref="DockerClaudeAgentOptions.PromptMountPoint"/>.
    /// </summary>
    internal (string HostPromptDir, string ContainerPromptPath) TranslateSystemPromptPath(
        string hostFilePath)
    {
        var hostDir = Path.GetDirectoryName(hostFilePath) ?? "";
        var fileName = Path.GetFileName(hostFilePath);
        var mountPoint = _options.PromptMountPoint.TrimEnd('/');
        var containerPath = $"{mountPoint}/{fileName}";
        return (hostDir, containerPath);
    }

    /// <summary>
    /// Generates a unique container name from the card ID and a random suffix.
    /// </summary>
    internal string BuildContainerName(string cardId)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"{_options.ContainerNamePrefix}-{tenant.ShortHash}-{cardId}-{suffix}";
    }

    /// <summary>
    /// Builds the full argument list for <c>docker run</c>, including volume mounts,
    /// image name, and the Claude CLI invocation.
    /// </summary>
    /// <param name="containerName">Generated container name.</param>
    /// <param name="hostPromptDir">Host directory containing system prompt files (mounted read-only).</param>
    /// <param name="claudeArgs">Claude CLI arguments.</param>
    /// <param name="mountContext">
    /// Optional workspace mount context from <see cref="DockerClaudeMountBuilder"/>.
    /// When provided, adds workspace/git/.git-override/credential mounts, env vars, and <c>-w /workspace</c>.
    /// </param>
    internal string[] BuildDockerArgumentList(
        string containerName,
        string hostPromptDir,
        string[] claudeArgs,
        DockerMountContext? mountContext = null)
    {
        var args = new List<string>
        {
            "run",
            "--rm",    // Remove container automatically on exit
            "--init",  // Use tini as PID 1 — forwards signals to children and reaps
                       // zombies. Without this, agent-spawned processes (e.g. test
                       // runners, GUI subprocesses) can outlive the CLI exit and
                       // hold open file handles on bind-mounted worktree files,
                       // blocking host-side cleanup. Reported on Windows Docker
                       // Desktop with Codex+Godot in v0.0.16.
            "-i",      // Attach stdin (required for prompt passthrough)
            "--name", containerName,
        };

        // Network mode — empty/null leaves Docker default (bridge); "host" gives the
        // container access to host-published ports (e.g. the docker-compose support
        // stack on localhost:5432, etc.).
        if (!string.IsNullOrWhiteSpace(_options.NetworkMode))
        {
            args.Add("--network");
            args.Add(_options.NetworkMode);
        }

        // Resource limits (optional)
        if (!string.IsNullOrWhiteSpace(_options.MemoryLimit))
        {
            args.Add("--memory");
            args.Add(_options.MemoryLimit);
        }
        if (!string.IsNullOrWhiteSpace(_options.CpuLimit))
        {
            args.Add("--cpus");
            args.Add(_options.CpuLimit);
        }

        // Container user (empty = image default)
        if (!string.IsNullOrWhiteSpace(_options.ContainerUser))
        {
            args.Add("--user");
            args.Add(_options.ContainerUser);
        }
        foreach (var group in _options.GroupAdd.Where(g => !string.IsNullOrWhiteSpace(g)))
        {
            args.Add("--group-add");
            args.Add(group);
        }

        if (_options.MountHostDockerSocket)
        {
            AddVolumeMount(
                args,
                "host Docker socket",
                new DockerMount
                {
                    HostPath = _options.HostDockerSocketPath,
                    ContainerPath = _options.ContainerDockerSocketPath,
                    ReadOnly = false,
                });
        }

        // Workspace, git, and credential mounts from DockerClaudeMountBuilder
        if (mountContext is not null)
        {
            foreach (var mount in mountContext.Mounts)
            {
                args.Add("-v");
                var spec = $"{mount.HostPath}:{mount.ContainerPath}";
                if (mount.ReadOnly) spec += ":ro";
                args.Add(spec);
            }

            // Environment variables (e.g., GIT_OPTIONAL_LOCKS=0)
            foreach (var (key, value) in mountContext.EnvironmentVariables)
            {
                args.Add("-e");
                args.Add($"{key}={value}");
            }

            // Set container working directory to the mounted worktree
            args.Add("-w");
            args.Add(DockerMountBuilderBase.WorkspaceMountPoint);
        }

        // Mount the system prompt directory read-only
        if (!string.IsNullOrEmpty(hostPromptDir))
        {
            args.Add("-v");
            args.Add($"{hostPromptDir}:{_options.PromptMountPoint}:ro");
        }

        // Additional configured mounts (operator-supplied extras)
        foreach (var (label, mount) in _options.AdditionalMounts)
        {
            AddVolumeMount(args, $"additional mount '{label}'", mount);
        }

        // Image to run
        args.Add(_options.ImageName);

        // Claude CLI executable inside the container
        args.Add(ContainerClaudeExecutable);

        // Claude CLI arguments (include model, schema, system prompt, etc.)
        args.AddRange(claudeArgs);

        return args.ToArray();
    }

    private void AddVolumeMount(List<string> args, string label, DockerMount mount)
    {
        if (string.IsNullOrEmpty(mount.HostPath) || string.IsNullOrEmpty(mount.ContainerPath))
        {
            logger.LogWarning(
                "Skipping incomplete {Label}: HostPath or ContainerPath is empty",
                label);
            return;
        }

        args.Add("-v");
        var spec = $"{mount.HostPath}:{mount.ContainerPath}";
        if (mount.ReadOnly) spec += ":ro";
        args.Add(spec);
    }

    /// <summary>
    /// Builds the Claude CLI argument list using the container-side system prompt path.
    /// Mirrors <see cref="ClaudeAgentExecutor.BuildArgumentList"/> with budget sourced
    /// from <see cref="DockerClaudeAgentOptions"/> instead of <see cref="ClaudeCliLlmOptions"/>.
    /// </summary>
    internal string[] BuildClaudeArgumentList(
        AgentExecutionContext context,
        string containerSystemPromptPath)
    {
        var budget = context.ProviderParams?.TryGetValue("maxBudget", out var mb) == true
            && decimal.TryParse(mb, System.Globalization.CultureInfo.InvariantCulture, out var parsedBudget)
            ? parsedBudget
            : _options.MaxBudgetUsd;

        var args = new List<string>
        {
            "--verbose",
            "--output-format", "stream-json",
            "--max-budget-usd", budget.ToString("F2"),
        };

        // Pass --model only when set. Cross-provider candidates may leave Model
        // null so the role's default doesn't leak into a different provider.
        if (!string.IsNullOrWhiteSpace(context.Model))
        {
            args.AddRange(["--model", context.Model]);
        }

        if (context.ProviderParams?.TryGetValue("permissionMode", out var pm) != true
            || !string.Equals(pm, "none", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["--permission-mode", "bypassPermissions", "--allowedTools", "*"]);
        }

        var schema = context.SchemaOverride ?? AgentSchemas.OutcomeSchema;
        args.AddRange([
            "--no-session-persistence",
            "--json-schema", AgentOutputParser.MinifyJson(schema),
            "--append-system-prompt-file", containerSystemPromptPath,
        ]);

        if (context.ProviderParams?.TryGetValue("effort", out var effort) == true)
            args.AddRange(["--effort", effort]);

        // Print mode: prompt delivered via stdin
        args.Add("--print");

        return args.ToArray();
    }
}
