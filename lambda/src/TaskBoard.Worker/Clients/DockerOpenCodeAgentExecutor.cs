using System.Text;
using Microsoft.Extensions.Options;

namespace TaskBoard.Worker.Clients;

/// <summary>
/// Executes the OpenCode CLI inside a Docker container, targeting a local
/// Anthropic-compatible llama.cpp server (e.g. Qwen3.6). Provider key:
/// <c>docker-opencode</c>.
/// </summary>
/// <remarks>
/// This executor mirrors <see cref="DockerClaudeAgentExecutor"/> structurally
/// but talks to a different CLI with different output conventions:
/// <list type="bullet">
///   <item>OpenCode has no native JSON-schema enforcement flag. The executor
///         prepends a schema-shaped instruction block to the prompt and
///         parses whatever structured output the model produces via
///         <see cref="OpenCodeOutputParser"/>.</item>
///   <item>On unparseable output, the executor retries up to
///         <see cref="DockerOpenCodeAgentOptions.MaxRetriesOnMalformedOutput"/>
///         times with progressively stricter re-prompts before returning
///         <c>{outcome: ERROR}</c> with raw output in detail.</item>
///   <item>Connection details for the llama-server are injected as env vars
///         by <see cref="DockerOpenCodeMountBuilder"/> and templated into
///         <c>~/.config/opencode/opencode.json</c> by the sandbox entrypoint.</item>
/// </list>
/// </remarks>
public sealed class DockerOpenCodeAgentExecutor(
    IOptions<DockerOpenCodeAgentOptions> options,
    ITenantIdentifier tenant,
    ILogger<DockerOpenCodeAgentExecutor> logger,
    DockerOpenCodeMountBuilder? mountBuilder = null,
    ProcessRunnerDelegate? processRunner = null) : IAgentExecutor
{
    private readonly DockerOpenCodeAgentOptions _options = options.Value;
    private readonly ProcessRunnerDelegate _runProcess = processRunner ?? ProcessRunner.RunProcessAsync;

    private const string DockerExecutable = "docker";
    private const string ContainerOpenCodeExecutable = "opencode";

    internal const int StopGracePeriodSeconds = 30;
    internal const int StopCommandTimeoutSeconds = StopGracePeriodSeconds + 5;
    internal const int RemoveCommandTimeoutSeconds = 10;

    /// <summary>
    /// Hint categories that indicate the upstream cannot serve this run as
    /// configured. Re-prompting cannot fix any of these — bail the retry loop
    /// early instead of burning the full retry budget on a doomed re-prompt.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    ///   <item><c>Network</c>: 502, connection refused, no route to host,
    ///         <c>llm-net</c> not found, request timeouts.</item>
    ///   <item><c>Auth</c>: token rejected. llama.cpp accepts any non-empty
    ///         token, so surfacing this means the env var isn't reaching the
    ///         container — re-prompting won't fix that.</item>
    ///   <item><c>Config</c>: OpenCode config references an unknown provider
    ///         key. A baked image config doesn't change between attempts.</item>
    ///   <item><c>Path</c>: 404 from llama-server. Wire-path mismatch between
    ///         OpenCode's adapter and the proxy's exposed routes.</item>
    /// </list>
    /// <c>Model</c> is intentionally NOT fatal — a model could be loaded
    /// mid-run on the local llama-server (rare but theoretically recoverable),
    /// so retries continue.
    /// </remarks>
    internal static readonly HashSet<string> FatalHintCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Network",
        "Auth",
        "Config",
        "Path",
    };

    public async Task<AgentResult> ExecuteAsync(
        AgentExecutionContext context, CancellationToken cancellationToken)
    {
        var containerName = BuildContainerName(context.TargetCardId);

        // The role's Model wins over the configured default — that's how a
        // workflow assigns specific roles to the no-think vs. -think Qwen alias.
        // Empty/whitespace falls back to the options default.
        var effectiveModel = !string.IsNullOrWhiteSpace(context.Model)
            ? context.Model
            : _options.ModelName;

        logger.LogInformation(
            "Launching Docker/OpenCode agent for card {CardId} in {Workspace}, " +
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
            // DockerClaudeAgentExecutor for the full rationale. OpenCode is
            // particularly sensitive: its file-resolution layer half-converts
            // Windows host paths (`C:\...\tasks\file.md` → `/C/...\tasks\file.md`),
            // making the failure mode silent until the agent times out.
            var promptContext = (mountContext is not null && context.CommentsFilePath is not null)
                ? context with { CommentsFilePath = mountContext.TranslatePath(context.CommentsFilePath) }
                : context;
            var basePrompt = BuildUserPromptWithSchema(promptContext, containerTaskFilePath);

            var (hostPromptDir, _) = TranslateSystemPromptPath(context.SystemPromptFilePath);
            var openCodeArgs = BuildOpenCodeArgumentList();
            var dockerArgs = BuildDockerArgumentList(
                containerName, hostPromptDir, openCodeArgs, mountContext);

            logger.LogDebug("Docker command: {Executable} {Args}",
                DockerExecutable, ProcessRunner.FormatArgsForLogging(dockerArgs));

            // Retry loop: re-prompt with a stricter instruction block when output
            // fails to parse as the Agent Contract JSON. Bounded by
            // MaxRetriesOnMalformedOutput so a badly-behaving model can't loop
            // forever.
            var attempt = 0;
            string lastStdout = "";
            var prompt = basePrompt;

            while (true)
            {
                int exitCode;
                string stdout, stderr;
                try
                {
                    (exitCode, stdout, stderr) = await _runProcess(
                        DockerExecutable, dockerArgs, context.WorkspacePath,
                        _options.TimeoutSeconds, cancellationToken,
                        stdinData: prompt,
                        envVarsToRemove: null,
                        agentName: $"Docker/OpenCode agent ({containerName})",
                        inactivityTimeoutSeconds: _options.InactivityTimeoutSeconds);
                }
                catch (TimeoutException)
                {
                    await StopAndRemoveContainerAsync(containerName);
                    AgentOutputParser.LogReproductionInfo(
                        logger, "Docker/OpenCode", DockerExecutable, dockerArgs,
                        prompt, context.WorkspacePath);
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
                        logger, "Docker/OpenCode", DockerExecutable, dockerArgs,
                        prompt, context.WorkspacePath);
                    throw;
                }

                lastStdout = stdout;

                // Stderr signature detection — surface likely root cause at the
                // top of the log so the operator sees it first.
                var hint = CliFailureHintDetector.Detect(
                    stderr, CliFailureHintDetector.OpenCodeSignatures);
                if (hint is not null)
                {
                    logger.LogError(
                        "OpenCode stderr matches known failure signature: {Category}. Hint: {Hint}",
                        hint.Category, hint.Hint);
                }

                // Fatal-hint short-circuit: when stderr indicates an unrecoverable
                // upstream problem (Network 502, Auth, Config, wire-Path), the
                // retry-on-malformed-output loop is doomed — re-prompting cannot
                // fix an unreachable server. Bail immediately as INFRASTRUCTURE
                // failure so AgentRunner restores the card to its trigger column
                // and the operator (or a fallback slot) gets a fast signal.
                // Without this, the original v0.0.22 KvA failure mode burns
                // 3 × inactivity-timer (~60 min) before surfacing the same error.
                if (hint is not null && FatalHintCategories.Contains(hint.Category))
                {
                    var fatalStderrSnippet = Truncate(stderr, 4000).Trim();
                    var fatalDetail = new StringBuilder(
                        $"OpenCode upstream unrecoverable ({hint.Category}). [Hint] {hint.Hint}");
                    if (!string.IsNullOrEmpty(fatalStderrSnippet))
                        fatalDetail.Append($"\nStderr: {fatalStderrSnippet}");

                    logger.LogError(
                        "Docker/OpenCode bailing retry loop on attempt {Attempt} due to fatal hint category {Category}",
                        attempt + 1, hint.Category);

                    AgentOutputParser.LogReproductionInfo(
                        logger, "Docker/OpenCode", DockerExecutable, dockerArgs,
                        prompt, context.WorkspacePath);

                    throw new CliInfrastructureException(fatalDetail.ToString());
                }

                if (exitCode != 0)
                {
                    if (!IsDockerExitCode(exitCode) && IsRateLimited(stderr))
                    {
                        var snippet = Truncate(stderr, 500).Trim();
                        logger.LogWarning(
                            "Docker/OpenCode rate limited for card {CardId}. Exit code {ExitCode}. Stderr: {Stderr}",
                            context.TargetCardId, exitCode, snippet);
                        throw new RateLimitException(
                            $"Docker/OpenCode rate limited (exit code {exitCode}). Stderr: {snippet}",
                            RateLimitSource.AgentCli);
                    }

                    logger.LogError(
                        "Docker/OpenCode agent exited with code {ExitCode}. Stderr: {Stderr}. Stdout: {Stdout}",
                        exitCode, Truncate(stderr, 10_000), Truncate(stdout, 2_000));

                    AgentOutputParser.LogReproductionInfo(
                        logger, "Docker/OpenCode", DockerExecutable, dockerArgs,
                        prompt, context.WorkspacePath);

                    var exitSource = IsDockerExitCode(exitCode) ? "Docker" : "OpenCode CLI";
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
                            "Docker/OpenCode rate limited for card {CardId} (exit 0, empty output). Stderr: {Stderr}",
                            context.TargetCardId, snippet);
                        throw new RateLimitException(
                            $"Docker/OpenCode rate limited (exit 0, empty output). Stderr: {snippet}",
                            RateLimitSource.AgentCli);
                    }

                    logger.LogError("Docker/OpenCode returned empty output. Stderr: {Stderr}",
                        Truncate(stderr, 4000));
                    AgentOutputParser.LogReproductionInfo(
                        logger, "Docker/OpenCode", DockerExecutable, dockerArgs,
                        prompt, context.WorkspacePath);
                    var emptyDetail = $"Docker/OpenCode returned empty output. Stderr: {Truncate(stderr, 4000).Trim()}";
                    if (hint is not null) emptyDetail = $"[Hint] {hint.Category}: {hint.Hint}\n{emptyDetail}";
                    throw new InvalidOperationException(emptyDetail);
                }

                logger.LogDebug("Docker/OpenCode raw stdout ({Length} chars):\n{Stdout}",
                    stdout.Length, Truncate(stdout, 10_000));

                var (resultJson, conversationLog) = OpenCodeOutputParser.Parse(stdout, logger);
                if (resultJson is not null)
                {
                    // AgentOutputParser.ParseResult expects either a `structured_output`
                    // wrapper (Claude/Codex shape) or a single outcome keyword. The
                    // OpenCode parser extracts a flat outcome object, so wrap it here
                    // so detail/questions/estimate/requestedSteps are all pulled through.
                    var wrapped = "{\"structured_output\":" + resultJson + "}";
                    var result = AgentOutputParser.ParseResult(wrapped)
                        with { ConversationLog = conversationLog };
                    logger.LogInformation(
                        "Docker/OpenCode complete (attempt {Attempt}), outcome={Outcome}, detail={Detail}, questions={QuestionCount}",
                        attempt + 1, result.Outcome, result.Detail ?? "(none)",
                        result.Questions?.Count ?? 0);
                    return result;
                }

                // Output had no parseable outcome. Before retrying or giving up, check
                // whether stderr indicates rate limiting — without this guard, an
                // upstream rate-limit that returns exit 0 + non-empty prose would
                // silently burn the retry budget and end up classified as a parse
                // failure instead of a rate-limit event.
                if (IsRateLimited(stderr))
                {
                    var snippet = Truncate(stderr, 500).Trim();
                    logger.LogWarning(
                        "Docker/OpenCode rate limited for card {CardId} on attempt {Attempt} " +
                        "(exit 0, no parseable outcome). Stderr: {Stderr}",
                        context.TargetCardId, attempt + 1, snippet);
                    throw new RateLimitException(
                        $"Docker/OpenCode rate limited (attempt {attempt + 1}, no parseable outcome). Stderr: {snippet}",
                        RateLimitSource.AgentCli);
                }

                // Output did not contain an outcome-bearing JSON object.
                if (attempt >= _options.MaxRetriesOnMalformedOutput)
                {
                    logger.LogError(
                        "Docker/OpenCode produced no parseable Agent Contract JSON after {Attempts} attempts. " +
                        "Returning ERROR outcome with raw stdout in detail.",
                        attempt + 1);
                    var rawDetail = "OpenCode CLI produced no parseable Agent Contract JSON after "
                        + $"{attempt + 1} attempt(s).\n\nRaw stdout (first 4000 chars):\n"
                        + Truncate(lastStdout, 4000);
                    return new AgentResult(
                        AgentOutcome.ERROR, rawDetail, null, lastStdout, null, null);
                }

                attempt++;
                logger.LogWarning(
                    "Docker/OpenCode output had no parseable outcome JSON (attempt {Attempt}/{Max}); retrying with stricter prompt",
                    attempt, _options.MaxRetriesOnMalformedOutput + 1);
                prompt = BuildStricterReprompt(basePrompt, lastStdout);
            }
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

    /// <summary>
    /// Docker-daemon-level exit codes: 125 (daemon error), 126 (command not
    /// invocable), 127 (command not found in image), 137 (SIGKILL / OOM).
    /// </summary>
    internal static bool IsDockerExitCode(int exitCode) =>
        exitCode is 125 or 126 or 127 or 137;

    /// <summary>
    /// Checks stderr for rate-limit signals. llama.cpp-fronted deployments
    /// usually don't rate-limit, but upstream proxies or shared hosting may
    /// surface Anthropic-style wording, which the Claude pattern list covers.
    /// Operators can extend via
    /// <see cref="DockerOpenCodeAgentOptions.RateLimitPatterns"/>.
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
        string[] openCodeArgs,
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
        args.Add(ContainerOpenCodeExecutable);
        args.AddRange(openCodeArgs);

        return args.ToArray();
    }

    /// <summary>
    /// OpenCode CLI arguments. Driven entirely from
    /// <see cref="DockerOpenCodeAgentOptions.CliArguments"/> so operators can
    /// adjust to CLI changes without recompiling. The task prompt is piped via
    /// stdin; there is no schema flag to pass.
    /// </summary>
    internal string[] BuildOpenCodeArgumentList() =>
        _options.CliArguments.ToArray();

    /// <summary>
    /// Builds the user prompt prefixed with an Agent Contract schema instruction
    /// block. OpenCode / llama.cpp has no server-side schema enforcement we can
    /// rely on in-process, so the contract is communicated in the prompt and
    /// validated by <see cref="OpenCodeOutputParser"/>.
    /// </summary>
    internal static string BuildUserPromptWithSchema(
        AgentExecutionContext context, string taskFilePath)
    {
        var sb = new StringBuilder();

        // System prompt inlined (OpenCode has no --append-system-prompt-file equivalent)
        if (!string.IsNullOrWhiteSpace(context.SystemPromptFilePath)
            && File.Exists(context.SystemPromptFilePath))
        {
            var systemPrompt = File.ReadAllText(context.SystemPromptFilePath);
            if (!string.IsNullOrWhiteSpace(systemPrompt))
            {
                sb.AppendLine("## System Instructions");
                sb.AppendLine();
                sb.AppendLine(systemPrompt.Trim());
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Response Contract");
        sb.AppendLine();
        sb.AppendLine(
            "Your FINAL response MUST end with a single JSON object (optionally inside " +
            "```json ... ``` fences) that conforms to this schema. No free text after the JSON.");
        sb.AppendLine();
        sb.AppendLine("```json");
        sb.Append(AgentOutputParser.MinifyJson(AgentSchemas.OutcomeSchema));
        sb.AppendLine();
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine(
            "The `outcome` field is required and must be one of `COMPLETE`, `NEEDS_INFO`, " +
            "or `ERROR`. Use `detail` for a GitHub-flavored markdown summary of what you " +
            "did or what you need. Do NOT invent additional fields.");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Task");
        sb.AppendLine();
        sb.AppendLine(context.TaskPrompt);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        PromptBuilder.AppendSharedSections(sb, context, taskFilePath);

        return sb.ToString();
    }

    /// <summary>
    /// Wraps the original prompt with an explicit retry preamble that names
    /// the previous output and demands a valid JSON response. Kept short so
    /// the model doesn't lose the original task context.
    /// </summary>
    internal static string BuildStricterReprompt(string basePrompt, string previousStdout)
    {
        var preview = previousStdout.Length > 1000
            ? previousStdout[..1000] + "...[truncated]"
            : previousStdout;

        var sb = new StringBuilder();
        sb.AppendLine("## Retry: Previous Response Was Not Parseable");
        sb.AppendLine();
        sb.AppendLine(
            "Your previous response did not contain a JSON object with an `outcome` field. " +
            "Please respond again, following the response contract exactly. The LAST thing " +
            "in your response MUST be a single JSON object matching the schema below, either " +
            "as the entire message or inside ```json ... ``` fences. Do not add any prose after it.");
        sb.AppendLine();
        sb.AppendLine("Previous response preview (do not repeat this shape):");
        sb.AppendLine("```");
        sb.AppendLine(preview);
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.Append(basePrompt);
        return sb.ToString();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...[truncated]");
}
