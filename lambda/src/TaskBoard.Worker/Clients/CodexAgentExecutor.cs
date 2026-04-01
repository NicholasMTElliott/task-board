using System.Diagnostics;
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

    private const string OutcomeSchema = """
        {
          "type": "object",
          "properties": {
            "outcome": {
              "type": "string",
              "enum": ["COMPLETE", "NEEDS_INFO", "ERROR"]
            },
            "detail": {
              "type": "string"
            },
            "questions": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "question": { "type": "string" },
                  "recommendations": {
                    "type": "array",
                    "items": { "type": "string" }
                  }
                },
                "required": ["question"]
              }
            },
            "requestedSteps": {
              "type": "array",
              "items": { "type": "string" }
            }
          },
          "required": ["outcome"]
        }
        """;

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
        if (combinedPrompt.Length > 20_000)
        {
            logger.LogWarning(
                "Combined prompt is {Length} chars — this may approach Windows CreateProcess limits (~32,767). " +
                "Consider reducing system prompt or task prompt length.",
                combinedPrompt.Length);
        }

        // Write schema to temp file (Codex uses --output-schema <filepath>)
        var schemaFilePath = Path.Combine(Path.GetTempPath(), $"codex-schema-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(schemaFilePath, MinifyJson(OutcomeSchema), cancellationToken);
            logger.LogDebug("Schema written to temp file: {SchemaFile}", schemaFilePath);

            var args = BuildArgumentList(context, schemaFilePath, combinedPrompt);

            logger.LogDebug("Codex CLI command: {FileName} {Args}",
                _options.ExecutablePath, FormatArgsForLogging(args));

            var (exitCode, stdout, stderr) = await RunProcessAsync(
                _options.ExecutablePath, args, context.WorkspacePath,
                _options.TimeoutSeconds, cancellationToken);

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
        sb.AppendLine("## Instructions");
        sb.AppendLine();
        sb.AppendLine($"- The target task file is at: {taskFilePath}");
        sb.AppendLine("- All project tasks are in the .aiboard/tasks/ directory for context.");

        if (context.CommentsFilePath is not null)
        {
            sb.AppendLine();
            sb.AppendLine("## Prior Conversation");
            sb.AppendLine();
            sb.AppendLine($"There is a conversation history file for this task at: {context.CommentsFilePath}.");
            sb.AppendLine();
            sb.AppendLine("This file has two sections:");
            sb.AppendLine("- **Reviewer Directives** — Comments from the human project operator. "
                + "These are AUTHORITATIVE. If a reviewer directive conflicts with any prior agent "
                + "recommendation or assumption, the reviewer directive takes precedence. "
                + "Always read and address reviewer directives before proceeding with your task.");
            sb.AppendLine("- **Agent History** — Output from prior agent runs (design documents, code "
                + "reviews, test results, gate checks). Use this for context about prior work, "
                + "but treat it as advisory, not authoritative.");
            sb.AppendLine();
            sb.AppendLine("**Read this file before starting work.** It is READ-ONLY — do not modify it.");
            sb.AppendLine();
        }

        sb.AppendLine("## Quality Gates");
        sb.AppendLine();
        sb.AppendLine("These are mandatory requirements. Do NOT return COMPLETE if any gate fails:");
        sb.AppendLine("- The project MUST build successfully.");
        sb.AppendLine("- All existing tests MUST pass.");
        sb.AppendLine("- New features MUST have test coverage that proves the requirements are met.");
        sb.AppendLine("- ALL requirements in the ticket description MUST be addressed — both the literal text and the spirit/intent.");
        sb.AppendLine();
        sb.AppendLine("## Outcome Rules");
        sb.AppendLine();
        sb.AppendLine("- **COMPLETE**: All quality gates pass and the work is fully done. Use this ONLY when there are zero blocking issues.");
        sb.AppendLine("- **NEEDS_INFO**: Any quality gate fails, any requirement is unmet, or you need answers before proceeding. Describe each issue as a question in the questions array with recommendations for resolution.");
        sb.AppendLine("- **ERROR**: Something went wrong that prevents you from doing the work at all (e.g. missing files, broken environment). Describe the issue in the detail field.");
        sb.AppendLine("- Always include a summary in the detail field of your structured response, regardless of outcome. This summary is posted as a comment on the ticket.");
        sb.AppendLine("- Format the detail field as GitHub-flavored markdown. Use headings, tables, bullet points, and code blocks as appropriate. This content is rendered directly on a GitHub issue.");

        return sb.ToString();
    }

    internal string[] BuildArgumentList(
        AgentExecutionContext context, string schemaFilePath, string combinedPrompt)
    {
        // codex exec [options] "<prompt>"
        // Flags must come before the prompt argument.
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

        // Approval policy: auto-approve edits without human prompting
        // Use providerParams override if provided, else global default
        var approvalPolicy = context.ProviderParams?.TryGetValue("approvalPolicy", out var ap) == true
            ? ap
            : _options.ApprovalPolicy;
        args.AddRange(["--approval-policy", approvalPolicy]);

        // The task/prompt — passed as final positional argument
        // Note: .NET ProcessStartInfo.ArgumentList bypasses cmd.exe quoting (~8KB limit does not apply);
        // Windows CreateProcess limit is ~32KB. Log a warning if the prompt is very large.
        args.Add(combinedPrompt);

        return args.ToArray();
    }

    /// <summary>
    /// Parses a single JSON message (from a NDJSON stream line or full stdout fallback)
    /// into an <see cref="AgentResult"/>.
    /// </summary>
    internal static AgentResult ParseResult(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            // Try structured_output first (preferred path — schema-validated response)
            if (root.TryGetProperty("structured_output", out var structured)
                && structured.ValueKind == JsonValueKind.Object
                && structured.TryGetProperty("outcome", out var structuredOutcome))
            {
                var outcome = ParseOutcomeString(structuredOutcome.GetString());
                var detail = structured.TryGetProperty("detail", out var d) ? d.GetString() : null;
                var questions = ParseQuestions(structured);
                var requestedSteps = ParseRequestedSteps(structured);
                return new AgentResult(outcome, detail, questions, null, requestedSteps);
            }

            // Try result field (some Codex event shapes wrap output here)
            if (root.TryGetProperty("result", out var result))
            {
                var resultText = result.GetString() ?? "";
                return new AgentResult(TryParseOutcomeFromText(resultText));
            }

            // Try output field (Responses API turn.completed shape)
            if (root.TryGetProperty("output", out var output))
            {
                var outputText = output.ValueKind == JsonValueKind.String
                    ? output.GetString() ?? ""
                    : output.ToString();
                return new AgentResult(TryParseOutcomeFromText(outputText));
            }
        }
        catch (JsonException)
        {
            // Raw text output — fall through to keyword scan
        }

        return new AgentResult(TryParseOutcomeFromText(stdout));
    }

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

    private static List<AgentQuestion>? ParseQuestions(JsonElement structured)
    {
        if (!structured.TryGetProperty("questions", out var questionsEl)
            || questionsEl.ValueKind != JsonValueKind.Array)
            return null;

        var questions = new List<AgentQuestion>();
        foreach (var item in questionsEl.EnumerateArray())
        {
            if (!item.TryGetProperty("question", out var q))
                continue;

            List<string>? recommendations = null;
            if (item.TryGetProperty("recommendations", out var recsEl)
                && recsEl.ValueKind == JsonValueKind.Array)
            {
                recommendations = [];
                foreach (var rec in recsEl.EnumerateArray())
                {
                    var val = rec.GetString();
                    if (val is not null)
                        recommendations.Add(val);
                }
            }

            questions.Add(new AgentQuestion(q.GetString()!, recommendations));
        }

        return questions.Count > 0 ? questions : null;
    }

    private static IReadOnlyList<string>? ParseRequestedSteps(JsonElement structured)
    {
        if (!structured.TryGetProperty("requestedSteps", out var stepsEl)
            || stepsEl.ValueKind != JsonValueKind.Array)
            return null;

        var steps = stepsEl.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString()!)
            .ToList();

        return steps.Count > 0 ? steps : null;
    }

    private static AgentOutcome ParseOutcomeString(string? outcome)
    {
        return outcome?.ToUpperInvariant() switch
        {
            "COMPLETE" => AgentOutcome.COMPLETE,
            "SUCCESS" => AgentOutcome.COMPLETE,       // backward compat
            "NEEDS_INFO" => AgentOutcome.NEEDS_INFO,
            "QUESTIONS" => AgentOutcome.NEEDS_INFO,   // backward compat
            "ERROR" => AgentOutcome.ERROR,
            _ => AgentOutcome.ERROR
        };
    }

    private static AgentOutcome TryParseOutcomeFromText(string text)
    {
        // Try parsing as JSON (might be schema-validated response or embedded in result field)
        try
        {
            var stripped = StripMarkdownFences(text.Trim());
            using var doc = JsonDocument.Parse(stripped);
            if (doc.RootElement.TryGetProperty("outcome", out var outcome))
            {
                return ParseOutcomeString(outcome.GetString());
            }
        }
        catch (JsonException) { }

        // Last resort: look for outcome keywords
        if (text.Contains("COMPLETE", StringComparison.OrdinalIgnoreCase)
            || text.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase))
            return AgentOutcome.COMPLETE;
        if (text.Contains("NEEDS_INFO", StringComparison.OrdinalIgnoreCase)
            || text.Contains("QUESTIONS", StringComparison.OrdinalIgnoreCase))
            return AgentOutcome.NEEDS_INFO;

        return AgentOutcome.ERROR;
    }

    private static string StripMarkdownFences(string text)
    {
        if (!text.StartsWith("```"))
            return text;

        var firstNewline = text.IndexOf('\n');
        if (firstNewline < 0)
            return text;

        var inner = text[(firstNewline + 1)..];
        var lastFence = inner.LastIndexOf("```");
        if (lastFence >= 0)
            inner = inner[..lastFence];

        return inner.Trim();
    }

    internal static string FormatArgsForLogging(string[] args)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < args.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            var arg = args[i];
            if (arg.Length > 2000)
                arg = arg[..2000] + "...[truncated]";
            sb.Append(arg.Contains(' ') ? $"\"{arg}\"" : arg);
        }
        return sb.ToString();
    }

    private static string MinifyJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string executable, string[] argumentList, string workingDirectory,
        int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var process = new Process();
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.FileName = executable;
        foreach (var arg in argumentList)
            startInfo.ArgumentList.Add(arg);

        process.StartInfo = startInfo;

        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuf.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuf.AppendLine(e.Data); };

        process.Start();

        // Begin async reads immediately to prevent stdout/stderr pipe buffer deadlock
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            throw new TimeoutException(
                $"Codex agent timed out after {timeoutSeconds}s. " +
                $"Partial stderr: {stderrBuf.ToString()[..Math.Min(500, stderrBuf.Length)]}");
        }

        return (process.ExitCode, stdoutBuf.ToString(), stderrBuf.ToString());
    }
}
