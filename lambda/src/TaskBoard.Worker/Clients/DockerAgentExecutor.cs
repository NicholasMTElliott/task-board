using System.Text;
using Microsoft.Extensions.Options;

namespace TaskBoard.Worker.Clients;

/// <summary>
/// Executes the Claude CLI inside a Docker container via <c>docker run</c>.
/// Reuses <see cref="AgentOutputParser"/> for NDJSON stream parsing and
/// <see cref="ClaudeAgentExecutor.IsRateLimited"/> for rate-limit detection.
/// </summary>
public sealed class DockerAgentExecutor(
    IOptions<DockerAgentOptions> options,
    ILogger<DockerAgentExecutor> logger,
    DockerMountBuilder? mountBuilder = null) : IAgentExecutor
{
    private readonly DockerAgentOptions _options = options.Value;

    private const string DockerExecutable = "docker";

    // Claude CLI is installed at this path inside the aiboard-sandbox image (#61).
    private const string ContainerClaudeExecutable = "claude";

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

            var userPrompt = ClaudeAgentExecutor.BuildUserPrompt(context, containerTaskFilePath);

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
                (exitCode, stdout, stderr) = await ProcessRunner.RunProcessAsync(
                    DockerExecutable, dockerArgs, context.WorkspacePath,
                    _options.TimeoutSeconds, cancellationToken,
                    stdinData: userPrompt,
                    envVarsToRemove: ["CLAUDECODE"],
                    agentName: $"Docker agent ({containerName})");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
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
    /// by mapping its parent directory to <see cref="DockerAgentOptions.PromptMountPoint"/>.
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
        return $"{_options.ContainerNamePrefix}-{cardId}-{suffix}";
    }

    /// <summary>
    /// Builds the full argument list for <c>docker run</c>, including volume mounts,
    /// image name, and the Claude CLI invocation.
    /// </summary>
    /// <param name="containerName">Generated container name.</param>
    /// <param name="hostPromptDir">Host directory containing system prompt files (mounted read-only).</param>
    /// <param name="claudeArgs">Claude CLI arguments.</param>
    /// <param name="mountContext">
    /// Optional workspace mount context from <see cref="DockerMountBuilder"/>.
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
            "-i",      // Attach stdin (required for prompt passthrough)
            "--name", containerName,
        };

        // Workspace, git, and credential mounts from DockerMountBuilder
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
            args.Add(DockerMountBuilder.WorkspaceMountPoint);
        }

        // Mount the system prompt directory read-only
        if (!string.IsNullOrEmpty(hostPromptDir))
        {
            args.Add("-v");
            args.Add($"{hostPromptDir}:{_options.PromptMountPoint}:ro");
        }

        // Additional configured mounts (e.g. workspace, credentials — see #65)
        foreach (var (label, mount) in _options.AdditionalMounts)
        {
            if (string.IsNullOrEmpty(mount.HostPath) || string.IsNullOrEmpty(mount.ContainerPath))
            {
                logger.LogWarning(
                    "Skipping incomplete additional mount '{Label}': HostPath or ContainerPath is empty",
                    label);
                continue;
            }

            args.Add("-v");
            var spec = $"{mount.HostPath}:{mount.ContainerPath}";
            if (mount.ReadOnly) spec += ":ro";
            args.Add(spec);
        }

        // Image to run
        args.Add(_options.ImageName);

        // Claude CLI executable inside the container
        args.Add(ContainerClaudeExecutable);

        // Claude CLI arguments (include model, schema, system prompt, etc.)
        args.AddRange(claudeArgs);

        return args.ToArray();
    }

    /// <summary>
    /// Builds the Claude CLI argument list using the container-side system prompt path.
    /// Mirrors <see cref="ClaudeAgentExecutor.BuildArgumentList"/> with budget sourced
    /// from <see cref="DockerAgentOptions"/> instead of <see cref="ClaudeCliLlmOptions"/>.
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
            "--model", context.Model,
            "--verbose",
            "--output-format", "stream-json",
            "--max-budget-usd", budget.ToString("F2"),
        };

        if (context.ProviderParams?.TryGetValue("permissionMode", out var pm) != true
            || !string.Equals(pm, "none", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["--permission-mode", "bypassPermissions", "--allowedTools", "*"]);
        }

        args.AddRange([
            "--no-session-persistence",
            "--json-schema", AgentOutputParser.MinifyJson(AgentSchemas.OutcomeSchema),
            "--append-system-prompt-file", containerSystemPromptPath,
        ]);

        if (context.ProviderParams?.TryGetValue("effort", out var effort) == true)
            args.AddRange(["--effort", effort]);

        // Print mode: prompt delivered via stdin
        args.Add("--print");

        return args.ToArray();
    }
}
