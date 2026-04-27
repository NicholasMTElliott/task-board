using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
                    agentName: "Codex agent");
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
                if (TryParseSingleDocumentStructured(stdout, out var singleDocResult))
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

                    var diagnostic = BuildNoStructuredOutputDiagnostic(stdout, stderr, hint);
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
    /// Parses the NDJSON stream emitted by <c>codex exec --json</c>.
    /// Codex emits events such as <c>thread.started</c>, <c>item.completed</c>,
    /// <c>turn.completed</c>, and <c>response.completed</c>. The exact event vocabulary
    /// may vary across versions — this parser is intentionally defensive.
    /// </summary>
    /// <summary>
    /// Top-level NDJSON event types we know how to handle. Anything outside this set
    /// is surfaced as a warning at end-of-parse so CLI-version drift is visible.
    /// </summary>
    private static readonly HashSet<string> KnownEventTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "thread.started",
        "turn.started",
        "turn.completed",
        "item.started",
        "item.completed",
        "item.updated",
        "response.completed",
        "response.started",
        "assistant",
        "user",
        "system",
    };

    internal (string? ResultJson, string ConversationLog) ParseStreamOutput(string stdout)
    {
        const int MaxConversationLogChars = 50_000;
        string? resultWithStructuredOutput = null;
        int resultWithStructuredOutputLine = 0;
        string? lastResultJson = null;
        int resultMessageCount = 0;
        int structuredOutputCount = 0;
        int malformedLineCount = 0;
        int nonEmptyLineCount = 0;
        var conversationLog = new StringBuilder();
        var lineNumber = 0;
        var unknownTypeCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var errorEventSamples = new List<string>();
        // Codex CLI 0.125.0+ emits the agent's structured-output JSON as the
        // text of the final item.completed whose item.type == "agent_message",
        // not as a top-level structured_output event. Track the most recent
        // one so we can fall back to it after the loop if no legacy
        // structured_output event was seen.
        string? lastAgentMessageText = null;
        int lastAgentMessageLine = 0;
        int agentMessageCount = 0;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            nonEmptyLineCount++;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                // Determine the event type (Codex NDJSON events carry a "type" field)
                var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

                logger.LogDebug("NDJSON line {LineNumber}: type={Type}, raw={Raw}",
                    lineNumber, type ?? "(no-type)", Truncate(trimmed, 500));

                // Track unknown top-level event types — CLI-version drift shows up here first.
                if (type is not null && !KnownEventTypes.Contains(type))
                {
                    unknownTypeCounts[type] = unknownTypeCounts.TryGetValue(type, out var c) ? c + 1 : 1;
                }

                // Error-shaped events always deserve visibility; capture a sample.
                if (type is not null
                    && type.Contains("error", StringComparison.OrdinalIgnoreCase)
                    && errorEventSamples.Count < 3)
                {
                    errorEventSamples.Add($"line {lineNumber}: {Truncate(trimmed, 500)}");
                }

                // Check for structured_output in any message
                var hasStructuredOutput = root.TryGetProperty("structured_output", out var so)
                    && so.ValueKind == JsonValueKind.Object
                    && so.TryGetProperty("outcome", out _);

                if (hasStructuredOutput)
                {
                    resultMessageCount++;
                    lastResultJson = trimmed;

                    var eventType = type ?? "(no-type)";
                    logger.LogInformation(
                        "NDJSON structured_output in event type={Type} #{Count} at line {LineNumber}: {Raw}",
                        eventType, structuredOutputCount + 1, lineNumber,
                        Truncate(trimmed, 2000));

                    if (structuredOutputCount == 0)
                    {
                        resultWithStructuredOutput = trimmed;
                        resultWithStructuredOutputLine = lineNumber;
                    }
                    else
                    {
                        logger.LogWarning(
                            "Multiple structured_output events! Previous at line {PrevLine}, current at line {CurrLine}. Using first.",
                            resultWithStructuredOutputLine, lineNumber);
                    }
                    structuredOutputCount++;
                }

                // Codex CLI 0.125.0+ shape: the agent's outcome JSON lives in
                // item.completed { item: { type: "agent_message", text: "<json>" } }.
                // Capture the LAST one so we can fall back after the loop if no
                // legacy structured_output event was seen. Multiple agent_messages
                // are common (intermediate reasoning); only the final one is
                // the canonical answer per the report from the field.
                if (string.Equals(type, "item.completed", StringComparison.OrdinalIgnoreCase)
                    && root.TryGetProperty("item", out var agentMsgItem)
                    && agentMsgItem.ValueKind == JsonValueKind.Object
                    && agentMsgItem.TryGetProperty("type", out var agentMsgType)
                    && string.Equals(agentMsgType.GetString(), "agent_message", StringComparison.OrdinalIgnoreCase)
                    && agentMsgItem.TryGetProperty("text", out var agentMsgText)
                    && agentMsgText.ValueKind == JsonValueKind.String)
                {
                    var text = agentMsgText.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        lastAgentMessageText = text;
                        lastAgentMessageLine = lineNumber;
                        agentMessageCount++;
                    }
                }

                // Extract text content for conversation log from known terminal event types
                // Codex may use: turn.completed, item.completed, response.completed
                // or wrap assistant text in "content" / "output" / "message" fields
                ExtractTextContent(root, type, conversationLog, MaxConversationLogChars, lineNumber);
            }
            catch (JsonException)
            {
                malformedLineCount++;
                logger.LogDebug("NDJSON line {LineNumber}: malformed JSON, skipping. Raw: {Raw}",
                    lineNumber, Truncate(trimmed, 200));
            }
        }

        // Codex CLI 0.125.0+ fallback: if no top-level structured_output event
        // was seen but we captured at least one agent_message, treat the LAST
        // agent_message's text as the structured outcome. Wrap it in a
        // synthetic { "structured_output": <parsed> } envelope so the existing
        // ParseResult / AgentOutputParser path consumes it unchanged.
        if (resultWithStructuredOutput is null && lastAgentMessageText is not null)
        {
            var stripped = AgentOutputParser.StripMarkdownFences(lastAgentMessageText.Trim());
            if (TryWrapAgentMessageAsStructured(stripped, out var wrapped))
            {
                logger.LogInformation(
                    "No top-level structured_output event found; using agent_message fallback " +
                    "(Codex CLI 0.125.0+ shape) from item.completed at line {Line} ({Count} agent_messages total).",
                    lastAgentMessageLine, agentMessageCount);
                resultWithStructuredOutput = wrapped;
                resultWithStructuredOutputLine = lastAgentMessageLine;
                structuredOutputCount = 1;
                resultMessageCount++;
            }
            else
            {
                logger.LogWarning(
                    "agent_message at line {Line} did not parse as a structured-outcome JSON " +
                    "(text length {Length} chars). Falling through to no-structured-output diagnostic.",
                    lastAgentMessageLine, lastAgentMessageText.Length);
            }
        }

        var resultJson = resultWithStructuredOutput ?? lastResultJson;

        if (resultWithStructuredOutput is not null && lastResultJson is not null
            && resultWithStructuredOutput != lastResultJson)
        {
            logger.LogWarning(
                "Used structured_output result from line {Line}, not the last result event. " +
                "Total result events: {Count}",
                resultWithStructuredOutputLine, resultMessageCount);
        }

        // Warn once about unknown event types — signals CLI-version drift.
        if (unknownTypeCounts.Count > 0)
        {
            var summary = string.Join(", ", unknownTypeCounts
                .OrderByDescending(kv => kv.Value)
                .Select(kv => $"{kv.Key}={kv.Value}"));
            logger.LogWarning(
                "NDJSON parsing saw {Count} unknown top-level event type(s): {Summary}. " +
                "This is likely Codex CLI version drift — update KnownEventTypes or verify parser coverage.",
                unknownTypeCounts.Count, summary);
        }

        // Warn if a meaningful share of non-empty lines was malformed (>10%, min 2 lines).
        // Occasional buffering noise is fine; systematic malformed output signals a bug.
        if (nonEmptyLineCount > 0 && malformedLineCount >= 2
            && (double)malformedLineCount / nonEmptyLineCount > 0.10)
        {
            logger.LogWarning(
                "NDJSON parsing saw {Malformed}/{Total} malformed lines ({Pct:P1}). " +
                "Codex stream output may be corrupt or buffered across event boundaries.",
                malformedLineCount, nonEmptyLineCount,
                (double)malformedLineCount / nonEmptyLineCount);
        }

        // Surface error-shaped events — these are sometimes the ONLY signal when
        // Codex reports a provider-side failure but exits 0.
        if (errorEventSamples.Count > 0)
        {
            logger.LogWarning(
                "NDJSON stream contained error-shaped event(s):\n{Samples}",
                string.Join("\n", errorEventSamples));
        }

        logger.LogInformation(
            "NDJSON parsing complete: {TotalLines} lines ({NonEmpty} non-empty, {Malformed} malformed), " +
            "resultEvents={ResultCount}, structuredOutputEvents={StructuredCount}, " +
            "agentMessages={AgentMessageCount}, unknownTypes={UnknownTypeCount}, " +
            "conversationLogChars={LogChars}",
            lineNumber, nonEmptyLineCount, malformedLineCount,
            resultMessageCount, structuredOutputCount, agentMessageCount,
            unknownTypeCounts.Count, conversationLog.Length);

        var log = conversationLog.Length > MaxConversationLogChars
            ? conversationLog.ToString()[..MaxConversationLogChars] + "\n...[truncated]"
            : conversationLog.ToString();

        return (resultJson, log);
    }

    /// <summary>
    /// Tries to extract assistant text content from a Codex NDJSON event for the conversation log.
    /// Handles multiple known Codex event shapes defensively.
    /// </summary>
    private void ExtractTextContent(
        JsonElement root, string? type, StringBuilder conversationLog,
        int maxChars, int lineNumber)
    {
        if (conversationLog.Length >= maxChars) return;

        // Shape 1: turn.completed / response.completed — "output" is array of content items
        // { "type": "turn.completed", "output": [{ "type": "output_text", "text": "..." }] }
        if (root.TryGetProperty("output", out var outputEl))
        {
            if (outputEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in outputEl.EnumerateArray())
                {
                    if (item.TryGetProperty("text", out var textEl))
                        AppendConversationText(conversationLog, textEl.GetString(), maxChars);
                }
                return;
            }
            if (outputEl.ValueKind == JsonValueKind.String)
            {
                AppendConversationText(conversationLog, outputEl.GetString(), maxChars);
                return;
            }
        }

        // Shape 2: item.completed — "item" contains the completed content item
        // { "type": "item.completed", "item": { "type": "output_text", "text": "..." } }
        if (root.TryGetProperty("item", out var itemEl))
        {
            if (itemEl.TryGetProperty("text", out var textEl))
                AppendConversationText(conversationLog, textEl.GetString(), maxChars);
            else if (itemEl.TryGetProperty("content", out var contentEl)
                && contentEl.ValueKind == JsonValueKind.String)
                AppendConversationText(conversationLog, contentEl.GetString(), maxChars);
            return;
        }

        // Shape 3: generic "message" wrapper (Responses API assistant message)
        // { "type": "...", "message": { "role": "assistant", "content": [{ "type": "text", "text": "..." }] } }
        if (root.TryGetProperty("message", out var messageEl))
        {
            if (messageEl.TryGetProperty("content", out var contentEl))
            {
                if (contentEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var block in contentEl.EnumerateArray())
                    {
                        if (block.TryGetProperty("type", out var blockType)
                            && blockType.GetString() == "text"
                            && block.TryGetProperty("text", out var textEl))
                            AppendConversationText(conversationLog, textEl.GetString(), maxChars);
                    }
                }
                else if (contentEl.ValueKind == JsonValueKind.String)
                {
                    AppendConversationText(conversationLog, contentEl.GetString(), maxChars);
                }
            }
            return;
        }

        // Shape 4: delta/streaming events — "delta" contains partial text
        if (type?.Contains("delta", StringComparison.OrdinalIgnoreCase) == true
            && root.TryGetProperty("delta", out var deltaEl)
            && deltaEl.TryGetProperty("text", out var deltaTextEl))
        {
            // Delta events are streaming partials — skip from conversation log to avoid noise
            logger.LogDebug("NDJSON line {LineNumber}: delta event skipped from conversation log", lineNumber);
        }
    }

    private static void AppendConversationText(StringBuilder sb, string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || sb.Length >= maxChars) return;
        if (sb.Length > 0) sb.AppendLine("\n---\n");
        sb.Append(text);
    }

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

    // Stderr signature detection moved to <see cref="CliFailureHintDetector"/>
    // for reuse across CLI executors. See <see cref="CliFailureHintDetector.CodexSignatures"/>.

    /// <summary>
    /// Codex CLI 0.125.0+ shape: the agent's structured-outcome JSON lives in the
    /// text of the final <c>item.completed</c> whose <c>item.type == "agent_message"</c>,
    /// not as a top-level <c>structured_output</c> event. Wrap that text into a
    /// synthetic <c>{ "structured_output": &lt;parsed&gt; }</c> envelope so the
    /// existing <see cref="AgentOutputParser.ParseResult"/> path consumes it
    /// unchanged. Returns false (and the caller falls through to the existing
    /// no-structured-output diagnostic) when the text doesn't parse as a JSON
    /// object with a recognised <c>outcome</c> enum value.
    /// </summary>
    private static bool TryWrapAgentMessageAsStructured(string text, out string wrapped)
    {
        wrapped = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("outcome", out var outcomeEl)) return false;
            if (outcomeEl.ValueKind != JsonValueKind.String) return false;
            var outcome = outcomeEl.GetString();
            if (outcome is not ("COMPLETE" or "NEEDS_INFO" or "ERROR" or "SUCCESS" or "QUESTIONS"))
                return false;
            wrapped = "{\"structured_output\":" + text + "}";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Backward-compat path: if stdout was a single JSON document (not NDJSON)
    /// containing a top-level <c>structured_output.outcome</c>, parse it. Returns
    /// false for anything else — callers should then fail loudly, not guess.
    /// </summary>
    private static bool TryParseSingleDocumentStructured(string stdout, out AgentResult result)
    {
        result = default!;
        var trimmed = stdout.Trim();
        if (trimmed.Length == 0 || trimmed[0] != '{') return false;

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (!root.TryGetProperty("structured_output", out var so)
                || so.ValueKind != JsonValueKind.Object
                || !so.TryGetProperty("outcome", out _))
                return false;
        }
        catch (JsonException)
        {
            return false;
        }

        result = AgentOutputParser.ParseResult(trimmed);
        return true;
    }

    /// <summary>
    /// Builds a compact, high-signal diagnostic block for the "no structured_output"
    /// failure path. Includes first/last stdout lines, line counts, stderr head,
    /// and any detected hint — everything an operator needs to reproduce locally.
    /// </summary>
    private static string BuildNoStructuredOutputDiagnostic(
        string stdout, string stderr, CliFailureHintDetector.FailureHint? hint)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Codex no-structured-output diagnostic ===");
        if (hint is not null)
            sb.AppendLine($"Hint: {hint.Category}: {hint.Hint}");

        var stdoutLines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        sb.AppendLine($"stdout: {stdout.Length} chars across {stdoutLines.Length} non-empty lines");

        if (stdoutLines.Length > 0)
        {
            sb.AppendLine("first 3 lines:");
            foreach (var line in stdoutLines.Take(3))
                sb.AppendLine($"  | {Truncate(line.Trim(), 500)}");

            if (stdoutLines.Length > 6)
            {
                sb.AppendLine("...");
                sb.AppendLine("last 3 lines:");
                foreach (var line in stdoutLines.Skip(Math.Max(0, stdoutLines.Length - 3)))
                    sb.AppendLine($"  | {Truncate(line.Trim(), 500)}");
            }
            else if (stdoutLines.Length > 3)
            {
                foreach (var line in stdoutLines.Skip(3))
                    sb.AppendLine($"  | {Truncate(line.Trim(), 500)}");
            }
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            sb.AppendLine($"stderr (first 2000 chars):");
            sb.AppendLine(Truncate(stderr, 2000));
        }

        sb.Append("===");
        return sb.ToString();
    }

}
