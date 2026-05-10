using System.Text;
using Microsoft.Extensions.Options;

namespace TaskBoard.Worker.Clients;

public sealed class ClaudeAgentExecutor(
    IOptions<ClaudeCliLlmOptions> options,
    ILogger<ClaudeAgentExecutor> logger,
    ProcessRunnerDelegate? processRunner = null) : IAgentExecutor
{
    private readonly ClaudeCliLlmOptions _options = options.Value;
    private readonly ProcessRunnerDelegate _runProcess = processRunner ?? ProcessRunner.RunProcessAsync;

    public async Task<AgentResult> ExecuteAsync(
        AgentExecutionContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Launching Claude agent for card {CardId} in {Workspace}, model={Model}",
            context.TargetCardId, context.WorkspacePath, context.Model);

        var taskFilePath = Processing.TaskFileManager.GetTaskFilePath(
            context.WorkspacePath, context.TargetCardId, context.TargetCardTitle);

        var args = BuildArgumentList(context, taskFilePath);
        var userPrompt = BuildUserPrompt(context, taskFilePath);

        logger.LogDebug("Claude CLI command: {FileName} {Args}",
            _options.ExecutablePath, ProcessRunner.FormatArgsForLogging(args));

        int exitCode;
        string stdout, stderr;
        try
        {
            (exitCode, stdout, stderr) = await _runProcess(
                _options.ExecutablePath, args, context.WorkspacePath,
                _options.TimeoutSeconds, cancellationToken,
                stdinData: userPrompt,
                envVarsToRemove: new[] { "CLAUDECODE" },
                agentName: "Claude agent",
                inactivityTimeoutSeconds: _options.InactivityTimeoutSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AgentOutputParser.LogReproductionInfo(
                logger, "Claude", _options.ExecutablePath, args,
                userPrompt, context.WorkspacePath);
            throw;
        }

        if (exitCode != 0)
        {
            if (IsRateLimited(stderr))
            {
                var snippet = stderr[..Math.Min(500, stderr.Length)].Trim();
                logger.LogWarning(
                    "Claude CLI rate limited for card {CardId}. Exit code {ExitCode}. Stderr: {Stderr}",
                    context.TargetCardId, exitCode, snippet);
                throw new RateLimitException(
                    $"Claude CLI rate limited (exit code {exitCode}). Stderr: {snippet}",
                    RateLimitSource.AgentCli);
            }

            // Stream-json mode emits the machine-readable rate-limit signal in stdout
            // (a `rate_limit_event` NDJSON line), not stderr. Without this branch the
            // 5-hour-window-rejected / out-of-credits shape gets misclassified as
            // AGENT_ERROR and the card moves to Problems instead of being held.
            if (IsRateLimitedStdout(stdout))
            {
                logger.LogWarning(
                    "Claude CLI rate limited for card {CardId} via stdout rate_limit_event. Exit code {ExitCode}.",
                    context.TargetCardId, exitCode);
                throw new RateLimitException(
                    $"Claude CLI rate limited (exit code {exitCode}, rate_limit_event in stdout).",
                    RateLimitSource.AgentCli);
            }

            logger.LogError("Claude agent exited with code {ExitCode}. Stderr: {Stderr}. Stdout: {Stdout}",
                exitCode, stderr, stdout[..Math.Min(500, stdout.Length)]);

            AgentOutputParser.LogReproductionInfo(
                logger, "Claude", _options.ExecutablePath, args,
                userPrompt, context.WorkspacePath);

            var stderrSnippet = stderr[..Math.Min(1000, stderr.Length)].Trim();
            var stdoutSnippet = stdout[..Math.Min(500, stdout.Length)].Trim();
            var detail = $"Claude CLI exited with code {exitCode}.";
            if (!string.IsNullOrEmpty(stderrSnippet))
                detail += $"\nStderr: {stderrSnippet}";
            if (!string.IsNullOrEmpty(stdoutSnippet))
                detail += $"\nStdout: {stdoutSnippet}";

            // Exit 126 (permission denied) / 127 (command not found) indicate
            // environment / installation problems, not agent-level failures.
            if (IsInfrastructureExitCode(exitCode))
                throw new CliInfrastructureException(detail);

            throw new InvalidOperationException(detail);
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            if (IsRateLimited(stderr))
            {
                var snippet = stderr[..Math.Min(500, stderr.Length)].Trim();
                logger.LogWarning(
                    "Claude CLI rate limited for card {CardId} (exit code 0, empty output). Stderr: {Stderr}",
                    context.TargetCardId, snippet);
                throw new RateLimitException(
                    $"Claude CLI rate limited (exit code 0, empty output). Stderr: {snippet}",
                    RateLimitSource.AgentCli);
            }

            logger.LogError("Claude agent returned empty output. Stderr: {Stderr}", stderr);
            AgentOutputParser.LogReproductionInfo(
                logger, "Claude", _options.ExecutablePath, args,
                userPrompt, context.WorkspacePath);
            throw new InvalidOperationException(
                $"Claude CLI returned empty output. Stderr: {stderr[..Math.Min(500, stderr.Length)].Trim()}");
        }

        logger.LogDebug("Claude agent raw stdout ({Length} chars):\n{Stdout}",
            stdout.Length, stdout[..Math.Min(10000, stdout.Length)]);

        var (resultJson, conversationLog) = ParseStreamOutput(stdout);

        if (!string.IsNullOrEmpty(conversationLog))
        {
            logger.LogInformation("Claude agent conversation ({Length} chars):\n{Log}",
                conversationLog.Length, conversationLog[..Math.Min(5000, conversationLog.Length)]);
        }

        var result = resultJson is not null
            ? ParseResult(resultJson)
            : ParseResult(stdout); // fallback: treat entire stdout as single JSON (backward compat)

        var resultWithLog = result with { ConversationLog = conversationLog };
        logger.LogInformation("Claude agent complete, outcome={Outcome}, detail={Detail}, questions={QuestionCount}",
            resultWithLog.Outcome, resultWithLog.Detail ?? "(none)", resultWithLog.Questions?.Count ?? 0);
        return resultWithLog;
    }

    internal static string BuildUserPrompt(AgentExecutionContext context, string taskFilePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine(context.TaskPrompt);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        PromptBuilder.AppendSharedSections(sb, context, taskFilePath);
        return sb.ToString();
    }

    internal string[] BuildArgumentList(AgentExecutionContext context, string taskFilePath)
    {
        // IMPORTANT: Flag-style args MUST come before content args (-p).
        // On Windows, claude.cmd runs through cmd.exe which misparses double quotes —
        // if a content arg with quotes appears early, all subsequent flags are corrupted.
        // Budget: use providerParams override if present, else global default
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
        // null so the role's default (e.g., a Claude model name) doesn't leak
        // into a different provider; in that case the CLI uses its own default.
        if (!string.IsNullOrWhiteSpace(context.Model))
        {
            args.AddRange(["--model", context.Model]);
        }

        // Permission/tools: omit for print-mode invocations (e.g., gate checks)
        if (context.ProviderParams?.TryGetValue("permissionMode", out var pm) != true
            || !string.Equals(pm, "none", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["--permission-mode", "bypassPermissions", "--allowedTools", "*"]);
        }

        var schema = context.SchemaOverride ?? AgentSchemas.OutcomeSchema;
        args.AddRange([
            "--no-session-persistence",
            "--json-schema", AgentOutputParser.MinifyJson(schema),
            "--append-system-prompt-file", context.SystemPromptFilePath,
        ]);

        // Provider-specific parameters from workflow config
        if (context.ProviderParams is not null)
        {
            if (context.ProviderParams.TryGetValue("effort", out var effort))
            {
                args.AddRange(["--effort", effort]);
            }
        }

        // Print mode: run non-interactively (prompt piped via stdin to avoid
        // Windows command-line length limits — cmd.exe caps at ~8 191 chars).
        args.Add("--print");

        return args.ToArray();
    }

    /// <summary>
    /// Returns true for shell-level exit codes that indicate the CLI binary
    /// could not be launched or exec'd (not an agent-level failure).
    /// </summary>
    internal static bool IsInfrastructureExitCode(int exitCode) =>
        exitCode is 126 or 127;

    /// <summary>
    /// Checks stderr for Claude CLI rate-limit signals (substring match — see
    /// <see cref="CliRateLimitDetector.ClaudePatterns"/>). Stderr-only by design:
    /// agent conversation prose flows through stdout and may mention "rate limit"
    /// in a way that has nothing to do with the request being rate-limited.
    /// </summary>
    /// <remarks>
    /// Pair with <see cref="IsRateLimitedStdout"/> at every exit-non-zero handler
    /// site: Claude CLI in <c>--output-format stream-json</c> emits its
    /// machine-readable rate-limit signal as a structured <c>rate_limit_event</c>
    /// in stdout, NOT stderr. Checking only stderr misses the
    /// <c>five_hour</c>-window-rejected and out-of-credits failure shapes.
    /// </remarks>
    internal static bool IsRateLimited(string stderr)
        => CliRateLimitDetector.Matches(stderr, CliRateLimitDetector.ClaudePatterns);

    /// <summary>
    /// Checks Claude CLI stdout NDJSON for a <c>rate_limit_event</c> whose
    /// <c>rate_limit_info.status</c> is not <c>"allowed"</c>. Thin shim over
    /// <see cref="AgentOutputParser.HasRejectedRateLimitEvent"/> for symmetry with
    /// <see cref="IsRateLimited"/> and for reuse by <see cref="DockerClaudeAgentExecutor"/>
    /// and <see cref="DockerClaudeQwenAgentExecutor"/>.
    /// </summary>
    internal static bool IsRateLimitedStdout(string stdout)
        => AgentOutputParser.HasRejectedRateLimitEvent(stdout);

    internal static AgentResult ParseResult(string stdout)
        => AgentOutputParser.ParseResult(stdout);

    internal (string? ResultJson, string ConversationLog) ParseStreamOutput(string stdout)
        => AgentOutputParser.ParseStreamOutput(stdout, logger);

}
