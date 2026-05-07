using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Tests.Helpers;
using TaskBoard.Worker.Validation;

namespace TaskBoard.Worker.Tests.Clients;

/// <summary>
/// Live smoke test against a real Claude CLI subprocess. Skipped by default —
/// see <see cref="CodexAgentExecutorLiveTests"/> for the rationale and the
/// <c>AIBOARD_TEST_LIVE_CLI=1</c> opt-in.
///
/// <para>
/// Confirms that today's installed Claude CLI version still emits stdout that
/// <see cref="AgentOutputParser.ParseStreamOutput"/> can extract a structured
/// outcome from. If a future Claude CLI version changes the wire shape (the way
/// Codex 0.125.0 did), this test will fail at the parser step before the change
/// reaches a user.
/// </para>
/// </summary>
public class ClaudeAgentExecutorLiveTests
{
    private static (string Workspace, string SystemPromptFile) NewWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "claude-live-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        var promptFile = Path.Combine(dir, "system.md");
        File.WriteAllText(promptFile,
            "You are a test agent. Respond with the structured outcome only — " +
            "no analysis, no edits. Set outcome to COMPLETE and detail to 'live-smoke-ok'.");
        return (dir, promptFile);
    }

    private static void CleanupWorkspace(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task LiveClaude_TrivialPrompt_ReturnsParseableOutcome()
    {
        if (!CliLiveTestGate.Enabled)
            return; // skip — set AIBOARD_TEST_LIVE_CLI=1 to enable

        // Auto-detect the executable the same way startup does.
        var executablePath = ClaudeCliResolver.Resolve("claude");
        var versionString = await ClaudeCliResolver.TryGetVersionAsync(executablePath);
        if (versionString.StartsWith("(unknown:", StringComparison.OrdinalIgnoreCase))
            return; // skip — claude CLI not installed or not on PATH

        // Log the policy classification so the test output records what was tested.
        var policyResult = CliVersionPolicy.Check(CliKey.Claude, versionString);

        var (workspace, promptFile) = NewWorkspace();
        try
        {
            var executor = new ClaudeAgentExecutor(
                TestOptionsMonitor.Create(new ClaudeCliLlmOptions
                {
                    ExecutablePath = executablePath,
                    TimeoutSeconds = 120,
                    MaxBudgetUsd = 0.50m,
                }),
                NullLogger<ClaudeAgentExecutor>.Instance);

            // Haiku is the cheapest tier and sufficient for "echo back the
            // structured outcome" smoke testing. Override via env var if the
            // operator's account doesn't have access.
            var model = Environment.GetEnvironmentVariable("AIBOARD_TEST_LIVE_CLAUDE_MODEL")
                ?? "claude-haiku-4-5-20251001";

            var context = new AgentExecutionContext(
                TargetCardId: "0",
                TargetCardTitle: "Live smoke test",
                WorkspacePath: workspace,
                TaskPrompt: "Respond with the structured outcome only — no analysis, no edits. " +
                            "Set outcome to COMPLETE and detail to 'live-smoke-ok'.",
                SystemPromptFilePath: promptFile,
                Model: model);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            var result = await executor.ExecuteAsync(context, cts.Token);

            Assert.True(
                result.Outcome is AgentOutcome.COMPLETE
                    or AgentOutcome.NEEDS_INFO
                    or AgentOutcome.ERROR,
                $"Claude CLI {versionString} (policy: {policyResult.Status}) " +
                $"returned an unexpected outcome: {result.Outcome}. " +
                $"Detail: {result.Detail}");
        }
        finally { CleanupWorkspace(workspace); }
    }
}
