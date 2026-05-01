using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace TaskBoard.Worker.Clients;

/// <summary>
/// Executes the OpenAI Codex CLI inside a Docker container via
/// <c>docker run -i --rm --init</c>. Provider key: <c>docker-codex</c>.
/// </summary>
/// <remarks>
/// Mirrors <see cref="CodexAgentExecutor"/>'s wire shape (NDJSON parsing via
/// <see cref="CodexOutputParser"/>, agent_message fallback, stdout-over-exit
/// recovery, version-drift warnings) but wraps the call in a Docker container
/// so the agent runs with <c>--yolo</c> safely — filesystem isolation is
/// provided by the container, not by the Codex CLI's own sandbox layer.
///
/// <para>
/// The image is <c>aiboard-codex-sandbox:latest</c> (see
/// <c>docker/codex-sandbox/Dockerfile</c>). Authentication comes from the
/// host's <c>~/.codex/</c> directory copied into a per-run staging dir by
/// <see cref="DockerCodexMountBuilder"/> and mounted as per-file read-only
/// mounts under the agent-owned <c>/home/agent/.codex</c> directory inside
/// the container. The directory itself is image-baked (Dockerfile pre-creates
/// it owned by <c>agent:agent</c>), so Codex CLI's runtime <c>mkdir sessions/</c>
/// succeeds on Docker Desktop Windows. The host's <c>~/.codex/</c> is never
/// mutated.
/// </para>
/// </remarks>
public sealed class DockerCodexAgentExecutor(
    IOptions<DockerCodexAgentOptions> options,
    ITenantIdentifier tenant,
    ILogger<DockerCodexAgentExecutor> logger,
    DockerCodexMountBuilder? mountBuilder = null,
    ProcessRunnerDelegate? processRunner = null) : IAgentExecutor
{
    private readonly DockerCodexAgentOptions _options = options.Value;
    private readonly ProcessRunnerDelegate _runProcess = processRunner ?? ProcessRunner.RunProcessAsync;

    private const string DockerExecutable = "docker";

    // Codex CLI is installed at this path inside the aiboard-codex-sandbox image
    // (npm install -g @openai/codex puts it in /usr/local/bin/codex).
    private const string ContainerCodexExecutable = "codex";

    internal const int StopGracePeriodSeconds = 30;
    internal const int StopCommandTimeoutSeconds = StopGracePeriodSeconds + 5;
    internal const int RemoveCommandTimeoutSeconds = 10;

    public async Task<AgentResult> ExecuteAsync(
        AgentExecutionContext context, CancellationToken cancellationToken)
    {
        var containerName = BuildContainerName(context.TargetCardId);

        logger.LogInformation(
            "Launching Docker/Codex agent for card {CardId} in {Workspace}, " +
            "model={Model}, image={Image}, container={Container}",
            context.TargetCardId, context.WorkspacePath,
            context.Model ?? "(provider default)", _options.ImageName, containerName);

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

        // Codex needs a schema file inside the container. Stage on host, mount RO,
        // and pass the container-side path. Could be in-memory if Codex supported
        // stdin-fed schemas, but it requires a real file path.
        var schemaSource = context.SchemaOverride ?? AgentSchemas.OutcomeSchemaOpenAI;
        var schemaJson = AgentOutputParser.MinifyJson(schemaSource);
        var schemaHash = Sha256Prefix(schemaJson);
        logger.LogInformation(
            "Codex output schema: {Length} chars, sha256={Hash}",
            schemaJson.Length, schemaHash);

        // Distinct prefix from host CodexAgentExecutor's `codex-schema-*.json`
        // pattern so test suites that count temp files by prefix don't collide
        // when both executors run in parallel.
        var hostSchemaFile = Path.Combine(Path.GetTempPath(), $"docker-codex-schema-{Guid.NewGuid():N}.json");
        const string ContainerSchemaPath = "/tmp/codex-schema.json";

        try
        {
            await File.WriteAllTextAsync(hostSchemaFile, schemaJson, cancellationToken);

            // Build combined prompt: same shape as host CodexAgentExecutor.
            //
            // Path translation: PromptBuilder.AppendSharedSections embeds
            // context.CommentsFilePath verbatim into the prompt. The host
            // path (e.g. C:\...\.aiboard\tasks\42-comments.md on Windows)
            // is unreadable inside a Linux container — the agent then
            // wastes its turn searching for the file via Glob and times
            // out. Translating to the container-side path
            // (/workspace/.aiboard/tasks/42-comments.md) avoids that.
            // Same fix applied to DockerClaude / DockerOpenCode.
            var taskFilePath = Processing.TaskFileManager.GetTaskFilePath(
                context.WorkspacePath, context.TargetCardId, context.TargetCardTitle);
            var containerTaskFilePath = mountContext?.TranslatePath(taskFilePath) ?? taskFilePath;

            var promptContext = (mountContext is not null && context.CommentsFilePath is not null)
                ? context with { CommentsFilePath = mountContext.TranslatePath(context.CommentsFilePath) }
                : context;
            var combinedPrompt = await BuildContainerPromptAsync(
                promptContext, containerTaskFilePath, cancellationToken);

            logger.LogInformation(
                "Codex combined prompt: {Length} chars ({KB} KB)",
                combinedPrompt.Length, combinedPrompt.Length / 1024);

            if (combinedPrompt.Length > 200_000)
            {
                logger.LogWarning(
                    "Codex combined prompt is very large ({Length} chars) — may hit CLI input limits.",
                    combinedPrompt.Length);
            }

            var codexArgs = BuildCodexArgumentList(context, ContainerSchemaPath);
            var dockerArgs = BuildDockerArgumentList(
                containerName, hostSchemaFile, ContainerSchemaPath, codexArgs, mountContext);

            logger.LogDebug("Docker command: {Executable} {Args}",
                DockerExecutable, ProcessRunner.FormatArgsForLogging(dockerArgs));

            int exitCode;
            string stdout, stderr;
            try
            {
                (exitCode, stdout, stderr) = await _runProcess(
                    DockerExecutable, dockerArgs, context.WorkspacePath,
                    _options.TimeoutSeconds, cancellationToken,
                    stdinData: combinedPrompt,
                    envVarsToRemove: _options.EnvVarsToRemove.Count > 0
                        ? _options.EnvVarsToRemove.ToArray()
                        : null,
                    agentName: $"Docker/Codex agent ({containerName})",
                    inactivityTimeoutSeconds: _options.InactivityTimeoutSeconds);
            }
            catch (TimeoutException)
            {
                await StopAndRemoveContainerAsync(containerName);
                AgentOutputParser.LogReproductionInfo(
                    logger, "Docker/Codex", DockerExecutable, dockerArgs,
                    combinedPrompt, context.WorkspacePath);
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
                    logger, "Docker/Codex", DockerExecutable, dockerArgs,
                    combinedPrompt, context.WorkspacePath);
                throw;
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                logger.LogInformation("Docker/Codex stderr ({Length} chars):\n{Stderr}",
                    stderr.Length, Truncate(stderr, 10_000));
            }

            var hint = CliFailureHintDetector.Detect(stderr, CliFailureHintDetector.CodexSignatures);
            if (hint is not null)
            {
                logger.LogError(
                    "Docker/Codex stderr matches known failure signature: {Category}. Hint: {Hint}",
                    hint.Category, hint.Hint);
            }

            if (exitCode != 0)
            {
                if (!IsDockerExitCode(exitCode) && IsRateLimited(stderr))
                {
                    var snippet = Truncate(stderr, 1000).Trim();
                    logger.LogWarning(
                        "Docker/Codex rate limited for card {CardId}. Exit {ExitCode}. Stderr: {Stderr}",
                        context.TargetCardId, exitCode, snippet);
                    throw new RateLimitException(
                        $"Docker/Codex rate limited (exit code {exitCode}). Stderr: {snippet}",
                        RateLimitSource.AgentCli);
                }

                // Stdout-over-exit recovery — same rationale as host CodexAgentExecutor.
                // Bypass for Docker daemon errors AND Codex infrastructure exit codes
                // (output untrustworthy when those fire).
                if (!IsDockerExitCode(exitCode)
                    && !ClaudeAgentExecutor.IsInfrastructureExitCode(exitCode)
                    && !string.IsNullOrWhiteSpace(stdout)
                    && CodexOutputParser.TryRecoverStructuredOutput(stdout, logger) is { } recovered)
                {
                    logger.LogWarning(
                        "Docker/Codex exited with code {ExitCode} but stdout contains a parseable " +
                        "structured outcome (outcome={Outcome}). Treating the parsed result as " +
                        "authoritative. Stderr: {Stderr}",
                        exitCode, recovered.Outcome, Truncate(stderr, 1000));
                    return recovered with { ConversationLog = recovered.ConversationLog ?? "" };
                }

                logger.LogError(
                    "Docker/Codex exited with code {ExitCode}. Stderr: {Stderr}. Stdout: {Stdout}",
                    exitCode, Truncate(stderr, 10_000), Truncate(stdout, 2000));

                AgentOutputParser.LogReproductionInfo(
                    logger, "Docker/Codex", DockerExecutable, dockerArgs,
                    combinedPrompt, context.WorkspacePath);

                var exitSource = IsDockerExitCode(exitCode) ? "Docker" : "Codex CLI";
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
                    var snippet = Truncate(stderr, 1000).Trim();
                    logger.LogWarning(
                        "Docker/Codex rate limited for card {CardId} (exit 0, empty output). Stderr: {Stderr}",
                        context.TargetCardId, snippet);
                    throw new RateLimitException(
                        $"Docker/Codex rate limited (exit 0, empty output). Stderr: {snippet}",
                        RateLimitSource.AgentCli);
                }

                logger.LogError("Docker/Codex returned empty stdout. Stderr: {Stderr}",
                    Truncate(stderr, 10_000));
                AgentOutputParser.LogReproductionInfo(
                    logger, "Docker/Codex", DockerExecutable, dockerArgs,
                    combinedPrompt, context.WorkspacePath);
                var emptyDetail = $"Docker/Codex returned empty output. Stderr: {Truncate(stderr, 4000).Trim()}";
                if (hint is not null)
                    emptyDetail = $"[Hint] {hint.Category}: {hint.Hint}\n{emptyDetail}";
                throw new InvalidOperationException(emptyDetail);
            }

            logger.LogDebug("Docker/Codex raw stdout ({Length} chars):\n{Stdout}",
                stdout.Length, Truncate(stdout, 10_000));

            var (resultJson, conversationLog) = CodexOutputParser.Parse(stdout, logger);

            if (!string.IsNullOrEmpty(conversationLog))
            {
                logger.LogInformation("Docker/Codex conversation ({Length} chars):\n{Log}",
                    conversationLog.Length, Truncate(conversationLog, 5000));
            }

            AgentResult result;
            if (resultJson is not null)
            {
                result = AgentOutputParser.ParseResult(resultJson);
            }
            else if (CodexOutputParser.TryParseSingleDocumentStructured(stdout, out var singleDocResult))
            {
                logger.LogInformation(
                    "Docker/Codex stdout parsed as single-document JSON with structured_output (no NDJSON stream).");
                result = singleDocResult;
            }
            else
            {
                AgentOutputParser.LogReproductionInfo(
                    logger, "Docker/Codex", DockerExecutable, dockerArgs,
                    combinedPrompt, context.WorkspacePath);

                var diagnostic = CodexOutputParser.BuildNoStructuredOutputDiagnostic(stdout, stderr, hint);
                logger.LogError(
                    "Docker/Codex stdout contained no structured_output. Refusing to guess outcome.\n{Diagnostic}",
                    diagnostic);
                throw new InvalidOperationException(
                    "Docker/Codex produced no structured_output event. " +
                    "See log for full diagnostic.\n" + diagnostic);
            }

            var resultWithLog = result with { ConversationLog = conversationLog };
            logger.LogInformation(
                "Docker/Codex complete, outcome={Outcome}, detail={Detail}, questions={QuestionCount}",
                resultWithLog.Outcome, resultWithLog.Detail ?? "(none)", resultWithLog.Questions?.Count ?? 0);
            return resultWithLog;
        }
        finally
        {
            try
            {
                if (File.Exists(hostSchemaFile))
                    File.Delete(hostSchemaFile);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete temp schema file: {SchemaFile}", hostSchemaFile);
            }

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
    /// Docker-daemon-level exit codes (not Codex CLI codes):
    /// 125 (daemon error), 126 (command not invocable), 127 (command not
    /// found in image), 137 (SIGKILL / OOM kill).
    /// </summary>
    internal static bool IsDockerExitCode(int exitCode) =>
        exitCode is 125 or 126 or 127 or 137;

    internal bool IsRateLimited(string stderr)
    {
        if (CliRateLimitDetector.Matches(stderr, CliRateLimitDetector.CodexDefaultPatterns))
            return true;
        if (_options.RateLimitPatterns.Count > 0
            && CliRateLimitDetector.Matches(stderr, _options.RateLimitPatterns))
            return true;
        return false;
    }

    internal string BuildContainerName(string cardId)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"{_options.ContainerNamePrefix}-{tenant.ShortHash}-{cardId}-{suffix}";
    }

    /// <summary>
    /// Builds the system+task prompt for Codex inside the container. Mirrors
    /// <see cref="CodexAgentExecutor.BuildCombinedPromptAsync"/> shape but
    /// takes a pre-translated container-side task file path.
    /// </summary>
    internal static async Task<string> BuildContainerPromptAsync(
        AgentExecutionContext context,
        string containerTaskFilePath,
        CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(context.SystemPromptFilePath)
            && File.Exists(context.SystemPromptFilePath))
        {
            var systemPrompt = await File.ReadAllTextAsync(context.SystemPromptFilePath, cancellationToken);
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

        sb.AppendLine("## Task");
        sb.AppendLine();
        sb.AppendLine(context.TaskPrompt);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        PromptBuilder.AppendSharedSections(sb, context, containerTaskFilePath);

        return sb.ToString();
    }

    /// <summary>
    /// Codex CLI argument list for in-container invocation. Same shape as
    /// <see cref="CodexAgentExecutor.BuildArgumentList"/> but sources
    /// yolo/fullAuto/sandbox from <see cref="DockerCodexAgentOptions"/>
    /// (which defaults <c>Yolo = true</c>).
    /// </summary>
    internal string[] BuildCodexArgumentList(
        AgentExecutionContext context, string containerSchemaPath)
    {
        var args = new List<string> { "exec" };

        if (!string.IsNullOrWhiteSpace(context.Model))
        {
            args.AddRange(["--model", context.Model]);
        }

        args.Add("--json");
        args.AddRange(["--output-schema", containerSchemaPath]);

        bool useYolo;
        string yoloSource;
        if (context.ProviderParams is not null
            && context.ProviderParams.TryGetValue("yolo", out var yoloStr))
        {
            useYolo = string.Equals(yoloStr, "true", StringComparison.OrdinalIgnoreCase);
            yoloSource = "providerParams";
        }
        else
        {
            useYolo = _options.Yolo;
            yoloSource = "config";
        }

        if (useYolo)
        {
            args.Add("--yolo");
        }

        bool useFullAuto;
        string fullAutoSource;
        if (context.ProviderParams is not null
            && context.ProviderParams.TryGetValue("fullAuto", out var fullAutoStr))
        {
            useFullAuto = string.Equals(fullAutoStr, "true", StringComparison.OrdinalIgnoreCase);
            fullAutoSource = "providerParams";
        }
        else
        {
            useFullAuto = _options.FullAuto;
            fullAutoSource = "config";
        }

        if (!useYolo && useFullAuto)
        {
            args.Add("--full-auto");
        }

        string? sandbox;
        string sandboxSource;
        if (context.ProviderParams is not null
            && context.ProviderParams.TryGetValue("sandbox", out var sandboxStr))
        {
            sandbox = sandboxStr;
            sandboxSource = "providerParams";
        }
        else
        {
            sandbox = _options.Sandbox;
            sandboxSource = string.IsNullOrWhiteSpace(_options.Sandbox) ? "(none)" : "config";
        }

        if (!useYolo && !string.IsNullOrWhiteSpace(sandbox))
        {
            args.AddRange(["--sandbox", sandbox]);
        }

        logger.LogInformation(
            "Docker/Codex effective policy: yolo={Yolo} ({YoloSrc}), fullAuto={FullAuto} ({FullAutoSrc}), sandbox={Sandbox} ({SandboxSrc})",
            useYolo, yoloSource,
            useFullAuto, fullAutoSource,
            string.IsNullOrWhiteSpace(sandbox) ? "(unset)" : sandbox, sandboxSource);

        if (useYolo && (useFullAuto || !string.IsNullOrWhiteSpace(sandbox)))
        {
            logger.LogWarning(
                "Docker/Codex --yolo is enabled; --full-auto and --sandbox flags will be omitted. " +
                "Configured fullAuto={FullAuto}, sandbox={Sandbox} have no effect in yolo mode.",
                useFullAuto, sandbox ?? "(unset)");
        }

        // Read prompt from stdin
        args.Add("-");

        return args.ToArray();
    }

    /// <summary>
    /// Builds the full <c>docker run</c> argument list including volume
    /// mounts, image, and the Codex CLI invocation.
    /// </summary>
    internal string[] BuildDockerArgumentList(
        string containerName,
        string hostSchemaFile,
        string containerSchemaPath,
        string[] codexArgs,
        DockerMountContext? mountContext = null)
    {
        var args = new List<string>
        {
            "run",
            "--rm",
            "--init",  // tini as PID 1 — see DockerClaudeAgentExecutor for rationale
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

        // Schema file mount (read-only). Codex `--output-schema` requires a
        // real file path, so we mount the host temp file as a single file.
        var normalizedSchemaPath = DockerMountBuilderBase.NormalizeHostPath(hostSchemaFile);
        args.Add("-v");
        args.Add($"{normalizedSchemaPath}:{containerSchemaPath}:ro");

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
        args.Add(ContainerCodexExecutable);
        args.AddRange(codexArgs);

        return args.ToArray();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...[truncated]");

    private static string Sha256Prefix(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }
}
