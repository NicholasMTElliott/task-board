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
              "enum": ["SUCCESS", "QUESTIONS", "ERROR"]
            },
            "detail": {
              "type": "string"
            }
          },
          "required": ["outcome"]
        }
        """;

    public async Task<AgentOutcome> ExecuteAsync(
        AgentExecutionContext context, CancellationToken cancellationToken)
    {
        logger.LogInformation(
            "Launching Claude agent for card {CardId} in {Workspace}, model={Model}",
            context.TargetCardId, context.WorkspacePath, context.Model);

        var taskFilePath = Processing.TaskFileManager.GetTaskFilePath(
            context.WorkspacePath, context.TargetCardId);

        var userPrompt = BuildUserPrompt(context, taskFilePath);
        var args = BuildArgumentList(context.Model, context.SystemPrompt, userPrompt);

        logger.LogDebug("Claude CLI command: {FileName} {Args}",
            OperatingSystem.IsWindows() ? "claude.cmd" : _options.ExecutablePath,
            FormatArgsForLogging(args));

        var (exitCode, stdout, stderr) = await RunProcessAsync(
            _options.ExecutablePath, args, context.WorkspacePath,
            _options.TimeoutSeconds, cancellationToken);

        if (exitCode != 0)
        {
            logger.LogError("Claude agent exited with code {ExitCode}. Stderr: {Stderr}. Stdout: {Stdout}",
                exitCode, stderr, stdout[..Math.Min(500, stdout.Length)]);
            throw new InvalidOperationException(
                $"Claude CLI exited with code {exitCode}. Stderr: {stderr[..Math.Min(1000, stderr.Length)]}");
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            logger.LogError("Claude agent returned empty output");
            throw new InvalidOperationException("Claude CLI returned empty output");
        }

        logger.LogDebug("Claude agent raw stdout ({Length} chars): {Stdout}",
            stdout.Length, stdout[..Math.Min(2000, stdout.Length)]);

        var outcome = ParseOutcome(stdout);
        logger.LogInformation("Claude agent complete, outcome={Outcome}", outcome);
        return outcome;
    }

    private static string BuildUserPrompt(AgentExecutionContext context, string taskFilePath)
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
        sb.AppendLine("- Modify ONLY the target task file. Do not modify any other file.");
        sb.AppendLine("- If you can complete the work fully and accurately, respond with outcome SUCCESS.");
        sb.AppendLine("- If you have important questions that must be answered first, add a ## Questions section to the task file and respond with outcome QUESTIONS.");
        sb.AppendLine("- If something goes wrong, add a ## Error section to the task file and respond with outcome ERROR.");
        return sb.ToString();
    }

    private string[] BuildArgumentList(string model, string systemPrompt, string userPrompt)
    {
        return
        [
            "-p", userPrompt,
            "--model", model,
            "--system-prompt", systemPrompt,
            "--output-format", "json",
            "--max-budget-usd", _options.MaxBudgetUsd.ToString("F2"),
            "--permission-mode", "bypassPermissions",
            "--allowedTools", "*",
            "--json-schema", MinifyJson(OutcomeSchema),
        ];
    }

    internal static AgentOutcome ParseOutcome(string stdout)
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
                return ParseOutcomeString(structuredOutcome.GetString());
            }

            // Try result field
            if (root.TryGetProperty("result", out var result))
            {
                var resultText = result.GetString() ?? "";
                return TryParseOutcomeFromText(resultText);
            }
        }
        catch (JsonException)
        {
            // Raw text output
        }

        return TryParseOutcomeFromText(stdout);
    }

    private static AgentOutcome ParseOutcomeString(string? outcome)
    {
        return outcome?.ToUpperInvariant() switch
        {
            "SUCCESS" => AgentOutcome.SUCCESS,
            "QUESTIONS" => AgentOutcome.QUESTIONS,
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
        if (text.Contains("SUCCESS", StringComparison.OrdinalIgnoreCase))
            return AgentOutcome.SUCCESS;
        if (text.Contains("QUESTIONS", StringComparison.OrdinalIgnoreCase))
            return AgentOutcome.QUESTIONS;

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
            if (arg.Length > 200)
                arg = arg[..200] + "...[truncated]";
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

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "claude.cmd";
            foreach (var arg in argumentList)
                startInfo.ArgumentList.Add(arg);
        }
        else
        {
            startInfo.FileName = executable;
            foreach (var arg in argumentList)
                startInfo.ArgumentList.Add(arg);
        }

        // Clear env vars that prevent Claude CLI from running as a subprocess
        startInfo.Environment.Remove("CLAUDECODE");

        process.StartInfo = startInfo;

        var stdoutBuf = new StringBuilder();
        var stderrBuf = new StringBuilder();

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuf.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuf.AppendLine(e.Data); };

        process.Start();
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
