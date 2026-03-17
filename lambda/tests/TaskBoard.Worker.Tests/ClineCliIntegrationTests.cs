using System.Diagnostics;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Tests.Helpers;
using Xunit.Abstractions;

namespace TaskBoard.Worker.Tests;

[Trait("Category", "Integration")]
public class ClineCliIntegrationTests(ITestOutputHelper output)
{
    private ClineCliLlmClient CreateSut()
    {
        var options = new ClineCliLlmOptions
        {
            ExecutablePath = "cline",
            TimeoutSeconds = 300
        };

        return new ClineCliLlmClient(
            Options.Create(options),
            new XUnitLogger<ClineCliLlmClient>(output));
    }

    private static bool IsClineAvailable()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            if (OperatingSystem.IsWindows())
            {
                startInfo.FileName = "cmd.exe";
                startInfo.Arguments = "/c cline --help";
            }
            else
            {
                startInfo.FileName = "cline";
                startInfo.Arguments = "--help";
            }

            using var process = Process.Start(startInfo);
            process?.WaitForExit(5000);
            return process is not null && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    [Fact]
    public async Task GetCompletionAsync_MinimalPrompt_ReturnsValidOutcome()
    {
        Assert.True(IsClineAvailable(), "Cline CLI ('cline') is not installed or not in PATH. Install with: npm install -g cline");
        var sut = CreateSut();

        var result = await sut.GetCompletionAsync(
            "claude-haiku-4-5-20251001",
            "You are a test agent. Always return COMPLETE with empty updates and a short summary.",
            "Test card. No real content.",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(result.Outcome, new[] { "COMPLETE", "NEEDS_INFO", "BLOCKED" });
        Assert.NotNull(result.SummaryComment);
        Assert.NotEmpty(result.SummaryComment);
        Assert.NotNull(result.Updates);
    }

    [Fact]
    public async Task GetCompletionAsync_NeedsInfoPrompt_ReturnsNeedsInfoOutcome()
    {
        Assert.True(IsClineAvailable(), "Cline CLI ('cline') is not installed or not in PATH. Install with: npm install -g cline");
        var sut = CreateSut();

        var result = await sut.GetCompletionAsync(
            "claude-haiku-4-5-20251001",
            "You are a test agent. Always return NEEDS_INFO. Put exactly one question in the updates dictionary under the key 'Questions'.",
            "Vague card with no details.",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("NEEDS_INFO", result.Outcome);
        Assert.NotNull(result.Updates);
        Assert.NotEmpty(result.Updates);
    }

    [Fact]
    public async Task GetCompletionAsync_WithCardSections_PreservesUpdateKeys()
    {
        Assert.True(IsClineAvailable(), "Cline CLI ('cline') is not installed or not in PATH. Install with: npm install -g cline");
        var sut = CreateSut();

        var result = await sut.GetCompletionAsync(
            "claude-haiku-4-5-20251001",
            "You are a test agent. Return COMPLETE. Put a short sentence in the updates dictionary under the key 'Requirements'.",
            "You are a test agent. Return COMPLETE. Put a short sentence in the updates dictionary under the key 'Requirements'. # Requirements\\nBuild a login page.\\n# Design\\nExisting design here.",
            CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("COMPLETE", result.Outcome);
        Assert.NotNull(result.Updates);
        Assert.True(result.Updates.Count > 0, "Expected at least one update key");
        Assert.NotNull(result.SummaryComment);
    }
}
