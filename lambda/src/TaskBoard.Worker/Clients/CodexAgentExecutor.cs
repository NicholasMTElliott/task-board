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
    ILogger<CodexAgentExecutor> logger) : IAgentExecutor
{
    private readonly CodexCliLlmOptions _options = options.Value;

    public async Task<AgentResult> ExecuteAsync(
        AgentExecutionContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Launching Codex agent for card {CardId} in {Workspace}, model={Model}",
            context.TargetCardId, context.WorkspacePath, context.Model);

        // Build combined prompt: system prompt prepended to task prompt
        // (Codex CLI has no --append-system-prompt-file equivalent)
        var combinedPrompt = await BuildCombinedPromptAsync(context, cancellationToken);

        logger.LogDebug("Combined prompt length: {Length} chars", combinedPrompt.Length);

        // Write schema to temp file (Codex uses --output-schema <filepath>)
        var schemaFilePath = Path.Combine(Path.GetTempPath(), $"codex-schema-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(schemaFilePath, AgentOutputParser.MinifyJson(AgentSchemas.OutcomeSchemaOpenAI), cancellationToken);
            logger.LogDebug("Schema written to temp file: {SchemaFile}", schemaFilePath);

            var args = BuildArgumentList(context, schemaFilePath);

            logger.LogDebug("Codex CLI command: {FileName} {Args}",
                _options.ExecutablePath, ProcessRunner.FormatArgsForLogging(args));

            // Pipe prompt via stdin ("-" arg) to avoid Windows command-line length limits
            var (exitCode, stdout, stderr) = await ProcessRunner.RunProcessAsync(
                _options.ExecutablePath, args, context.WorkspacePath,
                _options.TimeoutSeconds, cancellationToken,
                stdinData: combinedPrompt,
                agentName: "Codex agent");

            // Log stderr diagnostics regardless of exit code — Codex may emit useful info there
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                logger.LogInformation("Codex agent stderr ({Length} chars):\n{Stderr}",
                    stderr.Length, stderr[..Math.Min(5000, stderr.Length)]);
            }

            if (exitCode != 0)
            {
                logger.LogError(
                    "Codex agent exited with code {ExitCode}. Stderr: {Stderr}. Stdout: {Stdout}",
                    exitCode, stderr, stdout[..Math.Min(500, stdout.Length)]);

                var stderrSnippet = stderr[..Math.Min(1000, stderr.Length)].Trim();
                var stdoutSnippet = stdout[..Math.Min(500, stdout.Length)].Trim();
                var detail = $"Codex CLI exited with code {exitCode}.";
                if (!string.IsNullOrEmpty(stderrSnippet))
                    detail += $"\nStderr: {stderrSnippet}";
                if (!string.IsNullOrEmpty(stdoutSnippet))
                    detail += $"\nStdout: {stdoutSnippet}";

                throw new InvalidOperationException(detail);
            }

            if (string.IsNullOrWhiteSpace(stdout))
            {
                logger.LogError("Codex agent returned empty stdout. Stderr: {Stderr}", stderr);
                throw new InvalidOperationException(
                    $"Codex CLI returned empty output. Stderr: {stderr[..Math.Min(500, stderr.Length)].Trim()}");
            }

            logger.LogDebug("Codex agent raw stdout ({Length} chars):\n{Stdout}",
                stdout.Length, stdout[..Math.Min(10000, stdout.Length)]);

            var (resultJson, conversationLog) = ParseStreamOutput(stdout);

            if (!string.IsNullOrEmpty(conversationLog))
            {
                logger.LogInformation("Codex agent conversation ({Length} chars):\n{Log}",
                    conversationLog.Length, conversationLog[..Math.Min(5000, conversationLog.Length)]);
            }

            var result = resultJson is not null
                ? ParseResult(resultJson)
                : ParseResult(stdout); // fallback: treat entire stdout as single JSON (backward compat)

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

        // Automation preset: --full-auto enables workspace-write sandbox + on-request approvals.
        // providerParams can override via "fullAuto" (truthy string) or "sandbox" (explicit policy).
        var useFullAuto = context.ProviderParams?.TryGetValue("fullAuto", out var fa) == true
            ? string.Equals(fa, "true", StringComparison.OrdinalIgnoreCase)
            : _options.FullAuto;

        if (useFullAuto)
        {
            args.Add("--full-auto");
        }

        // Explicit sandbox policy overrides --full-auto's default sandbox level
        var sandbox = context.ProviderParams?.TryGetValue("sandbox", out var sb) == true
            ? sb
            : _options.Sandbox;

        if (!string.IsNullOrWhiteSpace(sandbox))
        {
            args.AddRange(["--sandbox", sandbox]);
        }

        // Read prompt from stdin
        args.Add("-");

        return args.ToArray();
    }

    internal static AgentResult ParseResult(string stdout)
        => AgentOutputParser.ParseResult(stdout);

    /// <summary>
    /// Parses the NDJSON stream emitted by <c>codex exec --json</c>.
    /// Codex emits events such as <c>thread.started</c>, <c>item.completed</c>,
    /// <c>turn.completed</c>, and <c>response.completed</c>. The exact event vocabulary
    /// may vary across versions — this parser is intentionally defensive.
    /// </summary>
    internal (string? ResultJson, string ConversationLog) ParseStreamOutput(string stdout)
    {
        const int MaxConversationLogChars = 50_000;
        string? resultWithStructuredOutput = null;
        int resultWithStructuredOutputLine = 0;
        string? lastResultJson = null;
        int resultMessageCount = 0;
        int structuredOutputCount = 0;
        var conversationLog = new StringBuilder();
        var lineNumber = 0;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            lineNumber++;
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                // Determine the event type (Codex NDJSON events carry a "type" field)
                var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;

                logger.LogDebug("NDJSON line {LineNumber}: type={Type}, raw={Raw}",
                    lineNumber, type ?? "(no-type)", trimmed[..Math.Min(500, trimmed.Length)]);

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
                        trimmed[..Math.Min(2000, trimmed.Length)]);

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

                // Extract text content for conversation log from known terminal event types
                // Codex may use: turn.completed, item.completed, response.completed
                // or wrap assistant text in "content" / "output" / "message" fields
                ExtractTextContent(root, type, conversationLog, MaxConversationLogChars, lineNumber);
            }
            catch (JsonException)
            {
                logger.LogDebug("NDJSON line {LineNumber}: malformed JSON, skipping. Raw: {Raw}",
                    lineNumber, trimmed[..Math.Min(200, trimmed.Length)]);
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

        logger.LogInformation(
            "NDJSON parsing complete: {TotalLines} lines, resultEvents={ResultCount}, " +
            "structuredOutputEvents={StructuredCount}, conversationLogChars={LogChars}",
            lineNumber, resultMessageCount, structuredOutputCount, conversationLog.Length);

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

}
