using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace TaskBoard.Worker.Clients;

/// <summary>
/// Agent executor backed by the OpenAI Codex CLI subprocess.
/// Uses <c>codex exec --json --output-schema &lt;file&gt;</c> for structured non-interactive output.
/// </summary>
public sealed class CodexAgentExecutor(
    IOptions<CodexCliLlmOptions> options,
    ILogger<CodexAgentExecutor> logger,
    ProcessRunnerDelegate? processRunner = null) : IAgentExecutor
{
    private readonly CodexCliLlmOptions _options = options.Value;
    private readonly ProcessRunnerDelegate _runProcess = processRunner ?? ProcessRunner.RunProcessAsync;

    public async Task<AgentResult> ExecuteAsync(
        AgentExecutionContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Launching Codex agent for card {CardId} in {Workspace}, model={Model}, timeoutSec={Timeout}",
            context.TargetCardId, context.WorkspacePath, context.Model, _options.TimeoutSeconds);

        // Sanity check: warn (do not fail) when the workspace is not a git checkout.
        // Codex tooling generally assumes a git worktree; silent success against a
        // non-git dir is a common confusion source.
        if (!IsGitWorkspace(context.WorkspacePath))
        {
            logger.LogWarning(
                "Workspace {Workspace} has no visible .git — Codex typically expects a git worktree. " +
                "Git-aware operations (diff, status, log) will fail inside the agent. " +
                "If this is intentional, ignore this warning.",
                context.WorkspacePath);
        }

        // Build combined prompt: system prompt prepended to task prompt
        // (Codex CLI has no --append-system-prompt-file equivalent)
        var combinedPrompt = await BuildCombinedPromptAsync(context, cancellationToken);

        logger.LogInformation(
            "Codex combined prompt: {Length} chars ({KB} KB)",
            combinedPrompt.Length, combinedPrompt.Length / 1024);

        if (combinedPrompt.Length > 200_000)
        {
            logger.LogWarning(
                "Codex combined prompt is very large ({Length} chars). " +
                "Large prompts may hit Codex CLI input limits or cause provider-side truncation. " +
                "Consider trimming system prompt or prior-conversation context.",
                combinedPrompt.Length);
        }

        var schemaSource = context.SchemaOverride ?? AgentSchemas.OutcomeSchemaOpenAI;
        var schemaJson = AgentOutputParser.MinifyJson(schemaSource);
        var schemaHash = Sha256Prefix(schemaJson);
        logger.LogInformation(
            "Codex output schema: {Length} chars, sha256={Hash}",
            schemaJson.Length, schemaHash);

        // Write schema to temp file (Codex uses --output-schema <filepath>)
        var schemaFilePath = Path.Combine(Path.GetTempPath(), $"codex-schema-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(schemaFilePath, schemaJson, cancellationToken);
            logger.LogDebug("Schema written to temp file: {SchemaFile}", schemaFilePath);

            var args = BuildArgumentList(context, schemaFilePath);

            logger.LogInformation("Codex CLI command: {FileName} {Args}",
                _options.ExecutablePath, ProcessRunner.FormatArgsForLogging(args));

            // Pipe prompt via stdin ("-" arg) to avoid Windows command-line length limits
            int exitCode;
            string stdout, stderr;
            try
            {
                (exitCode, stdout, stderr) = await _runProcess(
                    _options.ExecutablePath, args, context.WorkspacePath,
                    _options.TimeoutSeconds, cancellationToken,
                    stdinData: combinedPrompt,
                    envVarsToRemove: _options.EnvVarsToRemove.Count > 0
                        ? _options.EnvVarsToRemove.ToArray()
                        : null,
                    agentName: "Codex agent",
                    inactivityTimeoutSeconds: _options.InactivityTimeoutSeconds);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Timeout or other process-level failure — log repro info before propagating
                AgentOutputParser.LogReproductionInfo(
                    logger, "Codex", _options.ExecutablePath, args,
                    combinedPrompt, context.WorkspacePath);
                throw;
            }

            // Log stderr diagnostics regardless of exit code — Codex may emit useful info there
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                logger.LogInformation("Codex agent stderr ({Length} chars):\n{Stderr}",
                    stderr.Length, Truncate(stderr, 10_000));
            }

            // Pattern-match stderr for known failure signatures so the operator sees
            // an actionable hint instead of a raw dump. Purely advisory — the actual
            // exception is still thrown below based on exit/stdout shape.
            var hint = CliFailureHintDetector.Detect(stderr, CliFailureHintDetector.CodexSignatures);
            if (hint is not null)
            {
                logger.LogError(
                    "Codex stderr matches known failure signature: {Category}. Hint: {Hint}",
                    hint.Category, hint.Hint);
            }

            if (exitCode != 0)
            {
                if (IsRateLimited(stderr))
                {
                    var snippet = Truncate(stderr, 1000).Trim();
                    logger.LogWarning(
                        "Codex CLI rate limited for card {CardId}. Exit code {ExitCode}. Stderr: {Stderr}",
                        context.TargetCardId, exitCode, snippet);
                    throw new RateLimitException(
                        $"Codex CLI rate limited (exit code {exitCode}). Stderr: {snippet}",
                        RateLimitSource.AgentCli);
                }

                // Stdout-over-exit precedence: if the CLI exited non-zero but
                // stdout still contains a parseable structured outcome, the
                // agent's logical work succeeded — only the CLI process health
                // signal failed (e.g., transient network blip in a side request,
                // a sandbox warning, the rollout-tracking 'thread not found'
                // chatter Codex 0.125.0 emits, etc.). Prefer the parsed outcome
                // and surface the exit-code anomaly as a Warning so version
                // drift remains visible without killing legitimate runs.
                // Infrastructure exit codes (125/126/127/137 — see
                // ClaudeAgentExecutor.IsInfrastructureExitCode) bypass this
                // because they indicate the process never produced trustworthy
                // output (CLI not found, permission denied, OOM kill, etc.).
                if (!ClaudeAgentExecutor.IsInfrastructureExitCode(exitCode)
                    && !string.IsNullOrWhiteSpace(stdout)
                    && CodexOutputParser.TryRecoverStructuredOutput(stdout, logger) is { } recovered)
                {
                    logger.LogWarning(
                        "Codex CLI exited with code {ExitCode} but stdout contains a parseable " +
                        "structured outcome (outcome={Outcome}). Treating the parsed result as " +
                        "authoritative and proceeding. Stderr (first 1000 chars): {Stderr}",
                        exitCode, recovered.Outcome, Truncate(stderr, 1000));
                    return recovered with { ConversationLog = recovered.ConversationLog ?? "" };
                }

                logger.LogError(
                    "Codex agent exited with code {ExitCode}. Stderr: {Stderr}. Stdout: {Stdout}",
                    exitCode, Truncate(stderr, 10_000), Truncate(stdout, 2_000));

                AgentOutputParser.LogReproductionInfo(
                    logger, "Codex", _options.ExecutablePath, args,
                    combinedPrompt, context.WorkspacePath);

                var stderrSnippet = Truncate(stderr, 4000).Trim();
                var stdoutSnippet = Truncate(stdout, 2000).Trim();
                var detail = $"Codex CLI exited with code {exitCode}.";
                if (hint is not null)
                    detail += $"\n[Hint] {hint.Category}: {hint.Hint}";
                if (!string.IsNullOrEmpty(stderrSnippet))
                    detail += $"\nStderr: {stderrSnippet}";
                if (!string.IsNullOrEmpty(stdoutSnippet))
                    detail += $"\nStdout: {stdoutSnippet}";

                if (ClaudeAgentExecutor.IsInfrastructureExitCode(exitCode))
                    throw new CliInfrastructureException(detail);

                throw new InvalidOperationException(detail);
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                if (IsRateLimited(stderr))
                {
                    var snippet = Truncate(stderr, 1000).Trim();
                    logger.LogWarning(
                        "Codex CLI rate limited for card {CardId} (exit code 0, empty output). Stderr: {Stderr}",
                        context.TargetCardId, snippet);
                    throw new RateLimitException(
                        $"Codex CLI rate limited (exit code 0, empty output). Stderr: {snippet}",
                        RateLimitSource.AgentCli);
                }

                logger.LogError("Codex agent returned empty stdout. Stderr: {Stderr}",
                    Truncate(stderr, 10_000));
                AgentOutputParser.LogReproductionInfo(
                    logger, "Codex", _options.ExecutablePath, args,
                    combinedPrompt, context.WorkspacePath);
                var emptyDetail = $"Codex CLI returned empty output. Stderr: {Truncate(stderr, 4000).Trim()}";
                if (hint is not null)
                    emptyDetail = $"[Hint] {hint.Category}: {hint.Hint}\n{emptyDetail}";
                throw new InvalidOperationException(emptyDetail);
            }

            logger.LogDebug("Codex agent raw stdout ({Length} chars):\n{Stdout}",
                stdout.Length, Truncate(stdout, 10_000));

            var (resultJson, conversationLog) = ParseStreamOutput(stdout);

            if (!string.IsNullOrEmpty(conversationLog))
            {
                logger.LogInformation("Codex agent conversation ({Length} chars):\n{Log}",
                    conversationLog.Length, Truncate(conversationLog, 5000));
            }

            AgentResult result;
            if (resultJson is not null)
            {
                result = ParseResult(resultJson);
            }
            else
            {
                // No structured_output was seen in any NDJSON line. Before silently
                // falling back to text-keyword scanning (which can misclassify the
                // prompt echo as COMPLETE), try the backward-compat path: stdout as a
                // single JSON document with a top-level structured_output. Only that
                // path is safe; anything else blows up loudly.
                if (CodexOutputParser.TryParseSingleDocumentStructured(stdout, out var singleDocResult))
                {
                    logger.LogInformation(
                        "Codex stdout parsed as single-document JSON with structured_output (no NDJSON stream).");
                    result = singleDocResult;
                }
                else
                {
                    AgentOutputParser.LogReproductionInfo(
                        logger, "Codex", _options.ExecutablePath, args,
                        combinedPrompt, context.WorkspacePath);

                    var diagnostic = CodexOutputParser.BuildNoStructuredOutputDiagnostic(stdout, stderr, hint);
                    logger.LogError(
                        "Codex stdout contained no structured_output. Refusing to guess outcome.\n{Diagnostic}",
                        diagnostic);
                    throw new InvalidOperationException(
                        "Codex CLI produced no structured_output event. " +
                        "See log for full diagnostic.\n" + diagnostic);
                }
            }

            var resultWithLog = result with { ConversationLog = conversationLog };
            logger.LogInformation(
                "Codex agent complete, outcome={Outcome}, detail={Detail}, questions={QuestionCount}",
                resultWithLog.Outcome, resultWithLog.Detail ?? "(none)", resultWithLog.Questions?.Count ?? 0);
            return resultWithLog;
        }
        finally
        {
            // Clean up temp schema file
            try
            {
                if (File.Exists(schemaFilePath))
                    File.Delete(schemaFilePath);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete temp schema file: {SchemaFile}", schemaFilePath);
            }
        }
    }

    /// <summary>
    /// Builds the combined prompt by prepending system prompt file content to the task prompt.
    /// Codex CLI has no --append-system-prompt-file equivalent; system context is injected inline.
    /// </summary>
    internal static async Task<string> BuildCombinedPromptAsync(
        AgentExecutionContext context, CancellationToken cancellationToken)
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

        var taskFilePath = Processing.TaskFileManager.GetTaskFilePath(
            context.WorkspacePath, context.TargetCardId, context.TargetCardTitle);

        sb.AppendLine("## Task");
        sb.AppendLine();
        sb.AppendLine(context.TaskPrompt);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        PromptBuilder.AppendSharedSections(sb, context, taskFilePath);

        return sb.ToString();
    }

    internal string[] BuildArgumentList(
        AgentExecutionContext context, string schemaFilePath)
    {
        // codex exec [options] -
        // "-" tells codex to read the prompt from stdin (avoids Windows command-line length limits).
        var args = new List<string> { "exec" };

        // Model selection
        if (!string.IsNullOrWhiteSpace(context.Model))
        {
            args.AddRange(["--model", context.Model]);
        }

        // Structured JSON output (NDJSON stream of events)
        args.Add("--json");

        // Schema file for structured output validation
        args.AddRange(["--output-schema", schemaFilePath]);

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

        // Automation preset: --full-auto enables workspace-write sandbox + on-request approvals.
        // providerParams can override via "fullAuto" (truthy string) or "sandbox" (explicit policy).
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

        // Explicit sandbox policy overrides --full-auto's default sandbox level
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

        // Surface the effective policy so the operator can see — in logs at Info —
        // exactly what Codex is being asked to do, and where each value came from.
        // No silent defaults: values resolved from config are annotated as such.
        logger.LogInformation(
            "Codex effective policy: yolo={Yolo} ({YoloSrc}), fullAuto={FullAuto} ({FullAutoSrc}), sandbox={Sandbox} ({SandboxSrc})",
            useYolo, yoloSource,
            useFullAuto, fullAutoSource,
            string.IsNullOrWhiteSpace(sandbox) ? "(unset)" : sandbox, sandboxSource);

        if (useYolo && (useFullAuto || !string.IsNullOrWhiteSpace(sandbox)))
        {
            logger.LogWarning(
                "Codex --yolo is enabled; --full-auto and --sandbox flags will be omitted. " +
                "Configured fullAuto={FullAuto}, sandbox={Sandbox} have no effect in yolo mode.",
                useFullAuto, sandbox ?? "(unset)");
        }

        // Read prompt from stdin
        args.Add("-");

        return args.ToArray();
    }

    internal static AgentResult ParseResult(string stdout)
        => AgentOutputParser.ParseResult(stdout);

    /// <summary>
    /// Checks Codex CLI stderr for rate-limit signals.
    /// Merges <see cref="CliRateLimitDetector.CodexDefaultPatterns"/> with
    /// operator-supplied <see cref="CodexCliLlmOptions.RateLimitPatterns"/>.
    /// </summary>
    internal bool IsRateLimited(string stderr)
    {
        if (CliRateLimitDetector.Matches(stderr, CliRateLimitDetector.CodexDefaultPatterns))
            return true;
        if (_options.RateLimitPatterns.Count > 0
            && CliRateLimitDetector.Matches(stderr, _options.RateLimitPatterns))
            return true;
        return false;
    }

    /// <summary>
    /// Thin instance wrapper kept so existing call sites and tests continue to
    /// compile. Delegates to <see cref="CodexOutputParser.Parse"/>, which is
    /// shared with <see cref="DockerCodexAgentExecutor"/>.
    /// </summary>
    internal (string? ResultJson, string ConversationLog) ParseStreamOutput(string stdout)
        => CodexOutputParser.Parse(stdout, logger);

    // ─── Diagnostic helpers ─────────────────────────────────────────────────

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...[truncated]";

    /// <summary>
    /// Returns the first 12 hex chars of SHA256(text) — stable identifier suitable
    /// for correlating schema content across log lines ("did the schema change
    /// between the run that worked and the run that didn't?").
    /// </summary>
    private static string Sha256Prefix(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
    }

    /// <summary>
    /// True if the workspace directory appears to be a git checkout (has a .git
    /// file or directory, or an ancestor does). Non-throwing — returns false on
    /// any IO error.
    /// </summary>
    private static bool IsGitWorkspace(string workspacePath)
    {
        try
        {
            var dir = new DirectoryInfo(workspacePath);
            while (dir is not null)
            {
                var gitPath = Path.Combine(dir.FullName, ".git");
                if (File.Exists(gitPath) || Directory.Exists(gitPath))
                    return true;
                dir = dir.Parent;
            }
        }
        catch
        {
            // swallow — advisory check only
        }
        return false;
    }
}
