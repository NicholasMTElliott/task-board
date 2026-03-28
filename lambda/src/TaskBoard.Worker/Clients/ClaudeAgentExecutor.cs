using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace TaskBoard.Worker.Clients;

public sealed class ClaudeAgentExecutor(
    IOptions<ClaudeCliLlmOptions> options,
    ILogger<ClaudeAgentExecutor> logger) : IAgentExecutor
{
    private readonly ClaudeCliLlmOptions _options = options.Value;

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
            "Launching Claude agent for card {CardId} in {Workspace}, model={Model}",
            context.TargetCardId, context.WorkspacePath, context.Model);

        var taskFilePath = Processing.TaskFileManager.GetTaskFilePath(
            context.WorkspacePath, context.TargetCardId, context.TargetCardTitle);

        var args = BuildArgumentList(context, taskFilePath);
        var userPrompt = BuildUserPrompt(context, taskFilePath);

        logger.LogDebug("Claude CLI command: {FileName} {Args}",
            _options.ExecutablePath, FormatArgsForLogging(args));

        var (exitCode, stdout, stderr) = await RunProcessAsync(
            _options.ExecutablePath, args, context.WorkspacePath,
            _options.TimeoutSeconds, cancellationToken, stdinData: userPrompt);

        if (exitCode != 0)
        {
            logger.LogError("Claude agent exited with code {ExitCode}. Stderr: {Stderr}. Stdout: {Stdout}",
                exitCode, stderr, stdout[..Math.Min(500, stdout.Length)]);

            var stderrSnippet = stderr[..Math.Min(1000, stderr.Length)].Trim();
            var stdoutSnippet = stdout[..Math.Min(500, stdout.Length)].Trim();
            var detail = $"Claude CLI exited with code {exitCode}.";
            if (!string.IsNullOrEmpty(stderrSnippet))
                detail += $"\nStderr: {stderrSnippet}";
            if (!string.IsNullOrEmpty(stdoutSnippet))
                detail += $"\nStdout: {stdoutSnippet}";

            throw new InvalidOperationException(detail);
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            logger.LogError("Claude agent returned empty output. Stderr: {Stderr}", stderr);
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
            "--model", context.Model,
            "--verbose",
            "--output-format", "stream-json",
            "--max-budget-usd", budget.ToString("F2"),
        };

        // Permission/tools: omit for print-mode invocations (e.g., gate checks)
        if (context.ProviderParams?.TryGetValue("permissionMode", out var pm) != true
            || !string.Equals(pm, "none", StringComparison.OrdinalIgnoreCase))
        {
            args.AddRange(["--permission-mode", "bypassPermissions", "--allowedTools", "*"]);
        }

        args.AddRange([
            "--no-session-persistence",
            "--json-schema", MinifyJson(OutcomeSchema),
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

    internal static AgentResult ParseResult(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            // Try structured_output first (Claude CLI --output-format json envelope)
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

            // Try result field
            if (root.TryGetProperty("result", out var result))
            {
                var resultText = result.GetString() ?? "";
                return new AgentResult(TryParseOutcomeFromText(resultText));
            }
        }
        catch (JsonException)
        {
            // Raw text output
        }

        return new AgentResult(TryParseOutcomeFromText(stdout));
    }

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

                if (!root.TryGetProperty("type", out var typeEl))
                    continue;

                var type = typeEl.GetString();

                logger.LogDebug("NDJSON line {LineNumber}: type={Type}, raw={Raw}",
                    lineNumber, type, trimmed[..Math.Min(500, trimmed.Length)]);

                // Check for structured_output in every message
                var hasStructuredOutput = root.TryGetProperty("structured_output", out var so)
                    && so.ValueKind == JsonValueKind.Object
                    && so.TryGetProperty("outcome", out _);

                if (hasStructuredOutput && type != "result")
                {
                    logger.LogWarning(
                        "Found structured_output in unexpected message type={Type} at line {LineNumber}: {Raw}",
                        type, lineNumber, trimmed[..Math.Min(1000, trimmed.Length)]);
                }

                if (type == "result")
                {
                    resultMessageCount++;
                    lastResultJson = trimmed;
                    logger.LogInformation("NDJSON result message #{Count} at line {LineNumber}: {Raw}",
                        resultMessageCount, lineNumber, trimmed[..Math.Min(2000, trimmed.Length)]);

                    if (hasStructuredOutput)
                    {
                        structuredOutputCount++;
                        if (structuredOutputCount > 1)
                        {
                            logger.LogWarning(
                                "Multiple result messages with structured_output! " +
                                "Previous at line {PrevLine}, current at line {CurrLine}. Using first.",
                                resultWithStructuredOutputLine, lineNumber);
                        }
                        else
                        {
                            resultWithStructuredOutput = trimmed;
                            resultWithStructuredOutputLine = lineNumber;
                        }
                    }
                }
                else if (type == "assistant")
                {
                    // Assistant messages contain the model's text output
                    if (root.TryGetProperty("message", out var message)
                        && message.TryGetProperty("content", out var content)
                        && content.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var block in content.EnumerateArray())
                        {
                            if (block.TryGetProperty("type", out var blockType)
                                && blockType.GetString() == "text"
                                && block.TryGetProperty("text", out var text))
                            {
                                var textValue = text.GetString();
                                if (!string.IsNullOrEmpty(textValue) && conversationLog.Length < MaxConversationLogChars)
                                {
                                    if (conversationLog.Length > 0)
                                        conversationLog.AppendLine("\n---\n");
                                    conversationLog.Append(textValue);
                                }
                            }
                        }
                    }
                }
            }
            catch (JsonException)
            {
                logger.LogDebug("NDJSON line {LineNumber}: malformed JSON, skipping", lineNumber);
            }
        }

        // Prefer result message with structured_output; fall back to last result
        var resultJson = resultWithStructuredOutput ?? lastResultJson;

        if (resultWithStructuredOutput is not null && lastResultJson is not null
            && resultWithStructuredOutput != lastResultJson)
        {
            logger.LogWarning(
                "Used result from line {Line} (has structured_output), not the final result message. " +
                "Total result messages: {Count}",
                resultWithStructuredOutputLine, resultMessageCount);
        }

        logger.LogInformation(
            "NDJSON parsing complete: {TotalLines} lines, resultMessages={ResultCount}, " +
            "structuredOutputMessages={StructuredCount}, conversationLogChars={LogChars}",
            lineNumber, resultMessageCount, structuredOutputCount, conversationLog.Length);

        var log = conversationLog.Length > MaxConversationLogChars
            ? conversationLog.ToString()[..MaxConversationLogChars] + "\n...[truncated]"
            : conversationLog.ToString();

        return (resultJson, log);
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
        // Try parsing as JSON (might be embedded in result field)
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
        int timeoutSeconds, CancellationToken cancellationToken, string? stdinData = null)
    {
        using var process = new Process();
        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdinData is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        startInfo.FileName = executable;
        foreach (var arg in argumentList)
            startInfo.ArgumentList.Add(arg);

        // Clear env vars that prevent Claude CLI from running as a subprocess
        startInfo.Environment.Remove("CLAUDECODE");

        process.StartInfo = startInfo;

        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuf.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuf.AppendLine(e.Data); };

        process.Start();

        // Pipe prompt via stdin to avoid Windows command-line length limits
        if (stdinData is not null)
        {
            await process.StandardInput.WriteAsync(stdinData);
            process.StandardInput.Close();
        }

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
                $"Claude agent timed out after {timeoutSeconds}s. " +
                $"Partial stderr: {stderrBuf.ToString()[..Math.Min(500, stderrBuf.Length)]}");
        }

        return (process.ExitCode, stdoutBuf.ToString(), stderrBuf.ToString());
    }
}
