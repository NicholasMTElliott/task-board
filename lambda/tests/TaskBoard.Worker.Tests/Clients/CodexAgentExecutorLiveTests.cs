using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Tests.Helpers;
using TaskBoard.Worker.Validation;

namespace TaskBoard.Worker.Tests.Clients;

/// <summary>
/// Live smoke test against a real Codex CLI subprocess. Skipped by default.
/// Run before tagging a release or after upgrading the Codex CLI to confirm
/// our parser still extracts a structured outcome end-to-end.
///
/// <para>
/// Enable with <c>AIBOARD_TEST_LIVE_CLI=1 dotnet test</c>. Also requires
/// <c>codex</c> on PATH and Codex authentication (ChatGPT account or
/// <c>OPENAI_API_KEY</c>) configured. Each test invocation makes a real LLM
/// API call — keep prompts trivial.
/// </para>
/// </summary>
public class CodexAgentExecutorLiveTests
{
    private static (string Workspace, string SystemPromptFile) NewWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "codex-live-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        // Codex expects a git workspace for any tooling that reads diffs.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "init")
            {
                WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch { /* best-effort — codex will still warn but not fail */ }

        var promptFile = Path.Combine(dir, "system.md");
        File.WriteAllText(promptFile,
            "You are a test agent. Always reply with outcome=COMPLETE and a one-line detail. " +
            "Do not run any tools. Do not edit files.");
        return (dir, promptFile);
    }

    private static void CleanupWorkspace(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task LiveCodex_TrivialPrompt_ReturnsParseableOutcome()
    {
        if (!CliLiveTestGate.Enabled)
            return; // skip — set AIBOARD_TEST_LIVE_CLI=1 to enable

        var versionString = await CodexCliResolver.TryGetVersionAsync("codex");
        if (versionString.StartsWith("(unknown:", StringComparison.OrdinalIgnoreCase))
            return; // skip — codex CLI not installed or not on PATH

        // The whole point of the live test: catch the same wire-shape change
        // that 0.125.0 introduced before it lands on a user's run. Log the
        // version + policy classification so the test output records what
        // CLI was exercised.
        var policyResult = CliVersionPolicy.Check(CliKey.Codex, versionString);

        var (workspace, promptFile) = NewWorkspace();
        try
        {
            var executor = new CodexAgentExecutor(
                TestOptionsMonitor.Create(new CodexCliLlmOptions
                {
                    ExecutablePath = "codex",
                    // gpt-5.4-mini is the cheapest GPT-5 tier and is sufficient
                    // for "echo back the structured outcome" smoke testing.
                    // If unavailable in the operator's plan, override via
                    // AIBOARD_TEST_LIVE_CODEX_MODEL.
                    TimeoutSeconds = 120,
                }),
                NullLogger<CodexAgentExecutor>.Instance);

            var model = Environment.GetEnvironmentVariable("AIBOARD_TEST_LIVE_CODEX_MODEL")
                ?? "gpt-5.4-mini";

            var context = new AgentExecutionContext(
                TargetCardId: "0",
                TargetCardTitle: "Live smoke test",
                WorkspacePath: workspace,
                TaskPrompt: "Respond with the structured outcome only — no analysis, no edits. " +
                            "Set outcome to COMPLETE and detail to 'live-smoke-ok'.",
                SystemPromptFilePath: promptFile,
                Model: model,
                ProviderParams: new Dictionary<string, string>
                {
                    // Read-only sandbox: the smoke test must not edit the host.
                    ["sandbox"] = "read-only",
                });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(150));
            var result = await executor.ExecuteAsync(context, cts.Token);

            // Don't assert on the literal Detail value — model output varies. The
            // contract under test is purely "did the parser find a structured
            // outcome?" If this passes, the wire shape is still understood by
            // ParseStreamOutput; if it throws InvalidOperationException with
            // "no structured_output", the wire shape changed and we need a new
            // fixture + parser update.
            Assert.True(
                result.Outcome is AgentOutcome.COMPLETE
                    or AgentOutcome.NEEDS_INFO
                    or AgentOutcome.ERROR,
                $"Codex CLI {versionString} (policy: {policyResult.Status}) " +
                $"returned an unexpected outcome: {result.Outcome}. " +
                $"Detail: {result.Detail}");
        }
        finally { CleanupWorkspace(workspace); }
    }
}
