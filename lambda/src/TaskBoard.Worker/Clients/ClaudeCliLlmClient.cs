using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Clients;

public sealed class ClaudeCliLlmClient(
    IOptions<ClaudeCliLlmOptions> options,
    ILogger<ClaudeCliLlmClient> logger) : ILlmClient
{
    private readonly ClaudeCliLlmOptions _options = options.Value;
    private readonly ILogger<ClaudeCliLlmClient> _logger = logger;

    public async Task<AgentResponse> GetCompletionAsync(
        string model, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var fullSystemPrompt = systemPrompt;// + AgentResponseSchema.BuildSchemaInstruction();

        _logger.LogInformation("Calling Claude CLI model={Model}, executable={Executable}",
            model, _options.ExecutablePath);

        var argumentList = BuildArgumentList(model, fullSystemPrompt, userPrompt, _options);

        _logger.LogDebug("Claude CLI command: {FileName} {Args}",
            OperatingSystem.IsWindows() ? "claude.cmd" : _options.ExecutablePath,
            FormatArgsForLogging(argumentList));

        var (exitCode, stdout, stderr) = await RunProcessAsync(
            _options.ExecutablePath,
            argumentList,
            _options.TimeoutSeconds,
            cancellationToken);

        if (exitCode != 0)
        {
            _logger.LogError("Claude CLI exited with code {ExitCode}: {Stderr}", exitCode, stderr);
            throw new LlmApiException("claude-cli",
                $"Process exited with code {exitCode}: {stderr}");
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new LlmApiException("claude-cli", "Process returned empty output");
        }

        var contentText = ExtractStructuredOutput(stdout);

        if (!AgentResponseParser.TryParse(contentText, out var agentResponse, out var error))
        {
            _logger.LogError("Claude CLI raw stdout ({Length} chars): {Stdout}", stdout.Length, stdout[..Math.Min(500, stdout.Length)]);
            _logger.LogError("Extracted content ({ContentLength} chars): {Content}", contentText.Length, contentText[..Math.Min(500, contentText.Length)]);
            throw new LlmApiException("claude-cli", $"Failed to parse agent response: {error}");
        }

        _logger.LogInformation("Claude CLI call complete, outcome={Outcome}", agentResponse!.Outcome);
        return agentResponse;
    }

    internal static string[] BuildArgumentList(
        string model, string systemPrompt, string userPrompt, ClaudeCliLlmOptions options)
    {
        return
        [
            "-p", userPrompt,
            "--model", model,
            "--system-prompt", systemPrompt,
            "--output-format", "json",
            //"--max-turns", options.MaxTurns.ToString(),
            "--max-budget-usd", options.MaxBudgetUsd.ToString("F2"),
            "--permission-mode", "bypassPermissions",
            "--allowedTools", "*",
            "--json-schema", MinifyJson(AgentResponseSchema.JsonSchema),
            ];
    }

    internal static string ExtractStructuredOutput(string stdout)
    {
        try
        {
            using var doc = JsonDocument.Parse(stdout);
            var root = doc.RootElement;

            // Claude CLI --output-format json returns { "result": "...", "structured_output": {...} }
            if (root.TryGetProperty("structured_output", out var structured)
                && structured.ValueKind == JsonValueKind.Object)
            {
                return structured.GetRawText();
            }

            if (root.TryGetProperty("result", out var result))
            {
                var resultText = result.GetString()
                       ?? throw new LlmApiException("claude-cli", "Result field was null");
                return StripMarkdownFences(resultText);
            }
        }
        catch (JsonException)
        {
            // stdout might be raw text — return as-is for parser to handle
        }

        return StripMarkdownFences(stdout);
    }

    internal static string StripMarkdownFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```"))
            return text;

        // Strip opening fence (```json or ```)
        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
            return text;

        var inner = trimmed[(firstNewline + 1)..];

        // Strip closing fence
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

    internal static string MinifyJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunProcessAsync(
        string executable, string[] argumentList, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var process = new Process();
        var startInfo = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (OperatingSystem.IsWindows())
        {
            // On Windows, .cmd scripts invoked via ProcessStartInfo.ArgumentList go through
            // cmd.exe which mangles arguments containing quotes (like JSON).
            // Use PowerShell with single-quoted arguments instead — PowerShell handles
            // argument passing to .cmd files correctly without mangling.
            /*startInfo.FileName = "powershell.exe";
            var psArgs = string.Join(" ", argumentList.Select(EscapeArgForPowerShell));
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-Command");
            startInfo.ArgumentList.Add($"& {executable} {psArgs}");*/

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
            var partialOut = stdoutBuf.ToString();
            var partialErr = stderrBuf.ToString();
            throw new LlmApiException("claude-cli",
                $"Process timed out after {timeoutSeconds}s. " +
                $"Partial stdout ({partialOut.Length} chars): {partialOut[..Math.Min(500, partialOut.Length)]} | " +
                $"Partial stderr ({partialErr.Length} chars): {partialErr[..Math.Min(500, partialErr.Length)]}");
        }

        var stdout = stdoutBuf.ToString();
        return (process.ExitCode, stdout, stderrBuf.ToString());
    }

    internal static string EscapeArgForPowerShell(string arg)
    {
        // PowerShell single-quoted strings are literal — only escape is '' for a literal '
        return "'" + arg.Replace("'", "''") + "'";
    }
}
