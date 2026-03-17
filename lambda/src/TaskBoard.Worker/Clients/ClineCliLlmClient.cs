using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Clients;

public sealed class ClineCliLlmClient(
    IOptions<ClineCliLlmOptions> options,
    ILogger<ClineCliLlmClient> logger) : ILlmClient
{
    private readonly ClineCliLlmOptions _options = options.Value;
    private readonly ILogger<ClineCliLlmClient> _logger = logger;

    public async Task<AgentResponse> GetCompletionAsync(
        string model, string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        var combinedPrompt = BuildCombinedPrompt(systemPrompt, userPrompt);

        _logger.LogInformation("Calling Cline CLI model={Model}, executable={Executable}",
            model, _options.ExecutablePath);

        var (exitCode, stdout, stderr) = await RunProcessAsync(
            _options.ExecutablePath,
            BuildArgumentList(model, combinedPrompt, _options),
            _options.TimeoutSeconds,
            cancellationToken);

        if (exitCode != 0)
        {
            _logger.LogError("Cline CLI exited with code {ExitCode}: {Stderr}", exitCode, stderr);
            throw new LlmApiException("cline-cli",
                $"Process exited with code {exitCode}: {stderr}");
        }

        if (string.IsNullOrWhiteSpace(stdout))
        {
            throw new LlmApiException("cline-cli", "Process returned empty output");
        }

        var contentText = ExtractResponseFromNdjson(stdout);
        var normalizedJson = NormalizeResponse(contentText);

        if (!AgentResponseParser.TryParse(normalizedJson, out var agentResponse, out var error))
        {
            _logger.LogError("Cline CLI raw stdout ({Length} chars): {Stdout}", stdout.Length, stdout[..Math.Min(500, stdout.Length)]);
            _logger.LogError("Extracted content ({ContentLength} chars): {Content}", contentText.Length, contentText[..Math.Min(500, contentText.Length)]);
            _logger.LogError("Normalized JSON ({NormalizedLength} chars): {Normalized}", normalizedJson.Length, normalizedJson[..Math.Min(500, normalizedJson.Length)]);
            throw new LlmApiException("cline-cli", $"Failed to parse agent response: {error}");
        }

        _logger.LogInformation("Cline CLI call complete, outcome={Outcome}", agentResponse!.Outcome);
        return agentResponse;
    }

    internal static string BuildCombinedPrompt(string systemPrompt, string userPrompt) =>
        $"{systemPrompt}{AgentResponseSchema.BuildSchemaInstruction()}\n\nUser request:\n{userPrompt}";

    internal static string[] BuildArgumentList(
        string model, string combinedPrompt, ClineCliLlmOptions options)
    {
        return
        [
            "-y",
            "--json",
            "--model", model,
            "--timeout", options.TimeoutSeconds.ToString(),
            combinedPrompt
        ];
    }

    internal static string ExtractResponseFromNdjson(string stdout)
    {
        string? completionResultText = null;
        string? lastSayText = null;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;

                if (root.TryGetProperty("type", out var typeProp)
                    && typeProp.GetString() == "say"
                    && root.TryGetProperty("text", out var textProp))
                {
                    // Skip partial messages
                    if (root.TryGetProperty("partial", out var partialProp)
                        && partialProp.ValueKind == JsonValueKind.True)
                    {
                        continue;
                    }

                    var text = textProp.GetString();
                    if (string.IsNullOrEmpty(text))
                        continue;

                    // Prefer completion_result messages — they contain the final task output
                    if (root.TryGetProperty("say", out var saySubType)
                        && saySubType.GetString() == "completion_result")
                    {
                        completionResultText = text;
                    }

                    lastSayText = text;
                }
            }
            catch (JsonException)
            {
                // Skip non-JSON lines
            }
        }

        var rawText = completionResultText ?? lastSayText;

        if (rawText is null)
        {
            throw new LlmApiException("cline-cli",
                "No 'say' messages found in NDJSON output");
        }

        // Try markdown fence stripping first
        var stripped = StripMarkdownFences(rawText);

        // If the result is valid JSON, return it directly
        if (IsValidJson(stripped))
            return stripped;

        // Otherwise, try to extract embedded JSON object from conversational text
        var extracted = ExtractEmbeddedJson(stripped);
        if (extracted is not null)
            return extracted;

        return stripped;
    }

    internal static string NormalizeResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        using var ms = new MemoryStream();
        using var writer = new Utf8JsonWriter(ms);
        writer.WriteStartObject();

        // outcome: accept "outcome" or "status"
        var outcome = root.TryGetProperty("outcome", out var o) ? o
                    : root.TryGetProperty("status", out var s) ? s
                    : default;
        if (outcome.ValueKind != JsonValueKind.Undefined)
            writer.WriteString("outcome", outcome.GetString());

        // summaryComment: accept "summaryComment", "summary_comment", or "summary"
        var summary = root.TryGetProperty("summaryComment", out var sc) ? sc
                    : root.TryGetProperty("summary_comment", out var sc2) ? sc2
                    : root.TryGetProperty("summary", out var sm) ? sm
                    : default;
        if (summary.ValueKind != JsonValueKind.Undefined)
            writer.WriteString("summaryComment", summary.GetString());

        // updates: accept object as-is, convert array to empty object
        if (root.TryGetProperty("updates", out var updates))
        {
            if (updates.ValueKind == JsonValueKind.Object)
            {
                writer.WritePropertyName("updates");
                updates.WriteTo(writer);
            }
            else
            {
                writer.WriteStartObject("updates");
                writer.WriteEndObject();
            }
        }
        else
        {
            writer.WriteStartObject("updates");
            writer.WriteEndObject();
        }

        // approvalRequired: default to false if missing
        var approval = root.TryGetProperty("approvalRequired", out var ar) ? ar
                     : root.TryGetProperty("approval_required", out var ar2) ? ar2
                     : default;
        writer.WriteBoolean("approvalRequired",
            approval.ValueKind == JsonValueKind.True);

        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    internal static string? ExtractEmbeddedJson(string text)
    {
        // Find the first '{' and try to match to the last '}'
        var start = text.IndexOf('{');
        if (start < 0) return null;

        var end = text.LastIndexOf('}');
        if (end <= start) return null;

        var candidate = text[start..(end + 1)];
        return IsValidJson(candidate) ? candidate : null;
    }

    private static bool IsValidJson(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static string StripMarkdownFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```"))
            return text;

        var firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
            return text;

        var inner = trimmed[(firstNewline + 1)..];

        var lastFence = inner.LastIndexOf("```");
        if (lastFence >= 0)
            inner = inner[..lastFence];

        return inner.Trim();
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
            // On Windows, npm-installed CLIs are .cmd batch wrappers.
            // Use the .cmd file directly with ArgumentList — .NET handles escaping correctly.
            var execName = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                ? executable
                : executable + ".cmd";
            startInfo.FileName = execName;
            foreach (var arg in argumentList)
                startInfo.ArgumentList.Add(arg);
        }
        else
        {
            startInfo.FileName = executable;
            foreach (var arg in argumentList)
                startInfo.ArgumentList.Add(arg);
        }

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
            throw new LlmApiException("cline-cli",
                $"Process timed out after {timeoutSeconds}s. " +
                $"Partial stdout ({partialOut.Length} chars): {partialOut[..Math.Min(500, partialOut.Length)]} | " +
                $"Partial stderr ({partialErr.Length} chars): {partialErr[..Math.Min(500, partialErr.Length)]}");
        }

        return (process.ExitCode, stdoutBuf.ToString(), stderrBuf.ToString());
    }
}
