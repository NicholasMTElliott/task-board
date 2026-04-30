using System.Text;
using Microsoft.Extensions.Options;

namespace TaskBoard.Worker.Clients;

/// <summary>
/// Executes the Claude CLI inside a Docker container, but with the Anthropic
/// endpoint redirected to a local llama.cpp proxy (typically Qwen3.6 served by
/// the sibling <c>local-llm</c> compose project). Provider key:
/// <c>docker-claude-qwen</c>.
/// </summary>
/// <remarks>
/// Sits alongside <see cref="DockerClaudeAgentExecutor"/> (real Anthropic) and
/// <see cref="DockerOpenCodeAgentExecutor"/> (OpenCode → Qwen). The three are
/// deliberately separate so the candidate-evaluation feature can A/B them on
/// the same task and accumulate per-(role, provider) win rates.
///
/// <para>The CLI invocation is identical to the real-Anthropic case — same
/// <c>--json-schema</c>, same NDJSON parsing, same rate-limit detection, same
/// exit-code classification. The only differences are environmental: the mount
/// builder injects <c>ANTHROPIC_BASE_URL</c> + dummy auth + a synthetic
/// <c>~/.claude</c> dir, and the stderr signatures lean on Qwen / local-server
/// failure modes rather than real-Anthropic ones.</para>
/// </remarks>
public sealed class DockerClaudeQwenAgentExecutor(
    IOptions<DockerClaudeQwenAgentOptions> options,
    ITenantIdentifier tenant,
    ILogger<DockerClaudeQwenAgentExecutor> logger,
    DockerClaudeQwenMountBuilder? mountBuilder = null,
    ProcessRunnerDelegate? processRunner = null) : IAgentExecutor
{
    private readonly DockerClaudeQwenAgentOptions _options = options.Value;
    private readonly ProcessRunnerDelegate _runProcess = processRunner ?? ProcessRunner.RunProcessAsync;

    private const string DockerExecutable = "docker";
    private const string ContainerClaudeExecutable = "claude";

    internal const int StopGracePeriodSeconds = 30;
    internal const int StopCommandTimeoutSeconds = StopGracePeriodSeconds + 5;
    internal const int RemoveCommandTimeoutSeconds = 10;

    public async Task<AgentResult> ExecuteAsync(
        AgentExecutionContext context, CancellationToken cancellationToken)
    {
        var containerName = BuildContainerName(context.TargetCardId);

        // Per-call model selection: workflow role's Model wins over the
        // configured default, falling back to the options when empty. Plumbed
        // through to the mount builder so ANTHROPIC_MODEL points at the right
        // Qwen alias for this run.
        var effectiveModel = !string.IsNullOrWhiteSpace(context.Model)
            ? context.Model
            : _options.ModelName;

        logger.LogInformation(
            "Launching Docker/Claude→Qwen agent for card {CardId} in {Workspace}, " +
            "model={Model}, image={Image}, container={Container}, providerUrl={Url}",
            context.TargetCardId, context.WorkspacePath,
            effectiveModel, _options.ImageName, containerName, _options.ProviderBaseUrl);

        DockerMountContext? mountContext = null;
        if (mountBuilder is not null)
        {
            try
            {
                mountContext = await mountBuilder.BuildAsync(
                    context.WorkspacePath, _options, effectiveModel, cancellationToken);
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
            var taskFilePath = Processing.TaskFileManager.GetTaskFilePath(
                context.WorkspacePath, context.TargetCardId, context.TargetCardTitle);
            var containerTaskFilePath = mountContext?.TranslatePath(taskFilePath) ?? taskFilePath;

            // Mirror task-file translation for the comments file — see
            // DockerClaudeAgentExecutor for the full rationale.
            var promptContext = (mountContext is not null && context.CommentsFilePath is not null)
                ? context with { CommentsFilePath = mountContext.TranslatePath(context.CommentsFilePath) }
                : context;
            var userPrompt = ClaudeAgentExecutor.BuildUserPrompt(promptContext, containerTaskFilePath);

            var (hostPromptDir, containerPromptPath) = TranslateSystemPromptPath(
                context.SystemPromptFilePath);

            var claudeArgs = BuildClaudeArgumentList(context, effectiveModel, containerPromptPath);
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
                    // Strip CLAUDECODE so the CLI doesn't refuse to run as a subprocess
                    // (same guard as the real-Anthropic Docker executor).
                    envVarsToRemove: new[] { "CLAUDECODE" },
                    agentName: $"Docker/Claude→Qwen agent ({containerName})",
                    inactivityTimeoutSeconds: _options.InactivityTimeoutSeconds);
            }
            catch (TimeoutException)
            {
                await StopAndRemoveContainerAsync(containerName);
                AgentOutputParser.LogReproductionInfo(
                    logger, "Docker/Claude→Qwen", DockerExecutable, dockerArgs,
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
                    logger, "Docker/Claude→Qwen", DockerExecutable, dockerArgs,
                    userPrompt, context.WorkspacePath);
                throw;
            }

            // Stderr signature detection — Qwen-target patterns differ from
            // real-Anthropic patterns (network/proxy/local-model failures vs.
            // auth/quota). Surface the likely root cause at the top of logs.
            var hint = CliFailureHintDetector.Detect(
                stderr, CliFailureHintDetector.ClaudeQwenSignatures);
            if (hint is not null)
            {
                logger.LogError(
                    "Docker/Claude→Qwen stderr matches known failure signature: {Category}. Hint: {Hint}",
                    hint.Category, hint.Hint);
            }

            if (exitCode != 0)
            {
                if (!IsDockerExitCode(exitCode) && IsRateLimited(stderr))
                {
                    var snippet = Truncate(stderr, 500).Trim();
                    logger.LogWarning(
                        "Docker/Claude→Qwen rate limited for card {CardId}. Exit code {ExitCode}. Stderr: {Stderr}",
                        context.TargetCardId, exitCode, snippet);
                    throw new RateLimitException(
                        $"Docker/Claude→Qwen rate limited (exit code {exitCode}). Stderr: {snippet}",
                        RateLimitSource.AgentCli);
                }

                logger.LogError(
                    "Docker/Claude→Qwen agent exited with code {ExitCode}. Stderr: {Stderr}. Stdout: {Stdout}",
                    exitCode, Truncate(stderr, 10_000), Truncate(stdout, 2_000));

                AgentOutputParser.LogReproductionInfo(
                    logger, "Docker/Claude→Qwen", DockerExecutable, dockerArgs,
                    userPrompt, context.WorkspacePath);

                var exitSource = IsDockerExitCode(exitCode) ? "Docker" : "Claude CLI";
                var stderrSnippet = Truncate(stderr, 4000).Trim();
                var stdoutSnippet = Truncate(stdout, 2000).Trim();
                var detail = new StringBuilder($"{exitSource} exited with code {exitCode}.");
                if (hint is not null) detail.Append($"\n[Hint] {hint.Category}: {hint.Hint}");
                if (!string.IsNullOrEmpty(stderrSnippet)) detail.Append($"\nStderr: {stderrSnippet}");
                if (!string.IsNullOrEmpty(stdoutSnippet)) detail.Append($"\nStdout: {stdoutSnippet}");

                if (IsDockerExitCode(exitCode)
                    || ClaudeAgentExecutor.IsInfrastructureExitCode(exitCode))
                    throw new CliInfrastructureException(detail.ToString());

                throw new InvalidOperationException(detail.ToString());
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                if (IsRateLimited(stderr))
                {
                    var snippet = Truncate(stderr, 500).Trim();
                    logger.LogWarning(
                        "Docker/Claude→Qwen rate limited for card {CardId} (exit 0, empty output). Stderr: {Stderr}",
                        context.TargetCardId, snippet);
                    throw new RateLimitException(
                        $"Docker/Claude→Qwen rate limited (exit 0, empty output). Stderr: {snippet}",
                        RateLimitSource.AgentCli);
                }

                logger.LogError("Docker/Claude→Qwen returned empty output. Stderr: {Stderr}",
                    Truncate(stderr, 4000));
                AgentOutputParser.LogReproductionInfo(
                    logger, "Docker/Claude→Qwen", DockerExecutable, dockerArgs,
                    userPrompt, context.WorkspacePath);
                var emptyDetail = $"Docker/Claude→Qwen returned empty output. Stderr: {Truncate(stderr, 4000).Trim()}";
                if (hint is not null) emptyDetail = $"[Hint] {hint.Category}: {hint.Hint}\n{emptyDetail}";
                throw new InvalidOperationException(emptyDetail);
            }

            logger.LogDebug("Docker/Claude→Qwen raw stdout ({Length} chars):\n{Stdout}",
                stdout.Length, Truncate(stdout, 10_000));

            var (resultJson, conversationLog) = AgentOutputParser.ParseStreamOutput(stdout, logger);

            if (!string.IsNullOrEmpty(conversationLog))
            {
                logger.LogInformation("Docker/Claude→Qwen conversation ({Length} chars):\n{Log}",
                    conversationLog.Length, Truncate(conversationLog, 5000));
            }

            var result = resultJson is not null
                ? AgentOutputParser.ParseResult(resultJson)
                : AgentOutputParser.ParseResult(stdout);

            var resultWithLog = result with { ConversationLog = conversationLog };
            logger.LogInformation(
                "Docker/Claude→Qwen complete, outcome={Outcome}, detail={Detail}, questions={QuestionCount}",
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
    /// Best-effort container cleanup. Mirrors
    /// <see cref="DockerClaudeAgentExecutor.StopAndRemoveContainerAsync"/>.
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
                    "docker stop for container {ContainerName} threw; proceeding to docker rm -f",
                    containerName);
                stopExitCode = -1;
            }

            if (stopExitCode == 0)
            {
                logger.LogWarning("Container {ContainerName} stopped successfully", containerName);
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
                    "docker rm -f for container {ContainerName} threw; container may be orphaned",
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

    internal static bool IsDockerExitCode(int exitCode) =>
        exitCode is 125 or 126 or 127 or 137;

    /// <summary>
    /// Stderr-based rate-limit detection. Reuses the shared Anthropic patterns
    /// (the proxy speaks the Anthropic wire format) merged with operator-supplied
    /// extras.
    /// </summary>
    internal bool IsRateLimited(string stderr)
    {
        if (CliRateLimitDetector.Matches(stderr, CliRateLimitDetector.ClaudePatterns))
            return true;
        if (_options.RateLimitPatterns.Count > 0
            && CliRateLimitDetector.Matches(stderr, _options.RateLimitPatterns))
            return true;
        return false;
    }

    internal (string HostPromptDir, string ContainerPromptPath) TranslateSystemPromptPath(
        string hostFilePath)
    {
        var hostDir = Path.GetDirectoryName(hostFilePath) ?? "";
        var fileName = Path.GetFileName(hostFilePath);
        var mountPoint = _options.PromptMountPoint.TrimEnd('/');
        var containerPath = $"{mountPoint}/{fileName}";
        return (hostDir, containerPath);
    }

    internal string BuildContainerName(string cardId)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"{_options.ContainerNamePrefix}-{tenant.ShortHash}-{cardId}-{suffix}";
    }

    internal string[] BuildDockerArgumentList(
        string containerName,
        string hostPromptDir,
        string[] claudeArgs,
        DockerMountContext? mountContext = null)
    {
        var args = new List<string>
        {
            "run",
            "--rm",
            "--init",  // tini as PID 1 — signal forwarding + zombie reaping so
                       // agent-spawned children don't outlive the CLI and hold
                       // bind-mount file handles open. See DockerClaudeAgentExecutor.
            "-i",
            "--name", containerName,
        };

        if (!string.IsNullOrWhiteSpace(_options.NetworkMode))
        {
            args.Add("--network");
            args.Add(_options.NetworkMode);
        }
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
        if (!string.IsNullOrWhiteSpace(_options.ContainerUser))
        {
            args.Add("--user");
            args.Add(_options.ContainerUser);
        }

        if (mountContext is not null)
        {
            foreach (var mount in mountContext.Mounts)
            {
                args.Add("-v");
                var spec = $"{mount.HostPath}:{mount.ContainerPath}";
                if (mount.ReadOnly) spec += ":ro";
                args.Add(spec);
            }

            foreach (var (key, value) in mountContext.EnvironmentVariables)
            {
                args.Add("-e");
                args.Add($"{key}={value}");
            }

            args.Add("-w");
            args.Add(DockerMountBuilderBase.WorkspaceMountPoint);
        }

        if (!string.IsNullOrEmpty(hostPromptDir))
        {
            args.Add("-v");
            args.Add($"{hostPromptDir}:{_options.PromptMountPoint}:ro");
        }

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

        args.Add(_options.ImageName);
        args.Add(ContainerClaudeExecutable);
        args.AddRange(claudeArgs);

        return args.ToArray();
    }

    /// <summary>
    /// Builds the Claude CLI argument list. Same shape as
    /// <see cref="DockerClaudeAgentExecutor.BuildClaudeArgumentList"/> — same
    /// flags, same schema enforcement — but takes <paramref name="effectiveModel"/>
    /// explicitly so the per-call override surfaces on the command line in
    /// addition to the env var (Claude CLI's <c>--model</c> wins over
    /// <c>ANTHROPIC_MODEL</c>, which is what we want).
    /// </summary>
    internal string[] BuildClaudeArgumentList(
        AgentExecutionContext context,
        string effectiveModel,
        string containerSystemPromptPath)
    {
        var budget = context.ProviderParams?.TryGetValue("maxBudget", out var mb) == true
            && decimal.TryParse(mb, System.Globalization.CultureInfo.InvariantCulture, out var parsedBudget)
            ? parsedBudget
            : _options.MaxBudgetUsd;

        var args = new List<string>
        {
            "--model", effectiveModel,
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
            // Server-side schema enforcement via the proxy → llama.cpp tool-call
            // mechanism. This is the headline reason this executor exists: the
            // CLI flag is honoured the same way against Qwen as against real
            // Anthropic, so structured-output reliability matches the
            // real-Anthropic path (per the local-llm/Qwen-3.6.md card's 96.7–100%
            // tool-call benchmark).
            "--json-schema", AgentOutputParser.MinifyJson(context.SchemaOverride ?? AgentSchemas.OutcomeSchema),
            "--append-system-prompt-file", containerSystemPromptPath,
        ]);

        if (context.ProviderParams?.TryGetValue("effort", out var effort) == true)
            args.AddRange(["--effort", effort]);

        args.Add("--print");

        return args.ToArray();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...[truncated]");
}
