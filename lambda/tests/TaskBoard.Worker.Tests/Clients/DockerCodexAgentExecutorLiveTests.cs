using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Tests.Helpers;
using TaskBoard.Worker.Validation;

namespace TaskBoard.Worker.Tests.Clients;

/// <summary>
/// Live smoke test against a real <c>docker-codex</c> container. Skipped by
/// default — like its sibling <see cref="CodexAgentExecutorLiveTests"/>, this
/// is a release-readiness check, not a unit test.
/// </summary>
/// <remarks>
/// <para>
/// Enable with <c>AIBOARD_TEST_LIVE_CLI=1 dotnet test</c>. Also requires:
/// </para>
/// <list type="bullet">
///   <item>Docker daemon running (<c>docker info</c> succeeds)</item>
///   <item><c>aiboard-codex-sandbox:latest</c> image built —
///         <c>scripts/build-codex-sandbox.ps1</c></item>
///   <item>Host Codex authentication populated — <c>codex login</c> on the
///         host produces <c>~/.codex/auth.json</c> which the mount builder
///         stages into the container</item>
/// </list>
/// <para>
/// Each test invocation makes a real LLM API call. Keep prompts trivial. If
/// the operator's plan doesn't include the default <c>gpt-5.4-mini</c>, override
/// via <c>AIBOARD_TEST_LIVE_CODEX_MODEL</c>.
/// </para>
/// <para>
/// The unit test passes a stubbed <see cref="ProcessRunnerDelegate"/>; the
/// live test goes through the real <c>docker run</c> + Codex CLI path so it
/// catches anything that depends on the actual binary, image layout, or auth
/// flow — wire-shape regressions, image-tag drift, mount-builder corner cases,
/// and <c>codex --json</c> stream-format changes between Codex CLI versions.
/// </para>
/// </remarks>
public class DockerCodexAgentExecutorLiveTests
{
    private static async Task<bool> IsDockerAvailableAsync()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("docker", "info")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> IsImageBuiltAsync(string image)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("docker", $"image inspect {image}")
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p is null) return false;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasCodexAuth()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return false;
        var codexDir = Path.Combine(home, ".codex");
        return Directory.Exists(codexDir);
    }

    private static (string Workspace, string SystemPromptFile) NewWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(),
            "docker-codex-live-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        // The mount builder requires a real worktree (.git file) — `git init`
        // creates a base repo where .git is a directory; that's NOT a worktree.
        // We need the worktree shape: a parent base + a worktree off it. For
        // smoke testing the simpler base-repo shape works because the builder
        // logs a warning but proceeds without the worktree-specific .git
        // override; the container runs against /workspace without git history.
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "init")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(5000);
        }
        catch { /* best-effort */ }

        var promptFile = Path.Combine(dir, "system.md");
        File.WriteAllText(promptFile,
            "You are a test agent running inside an isolated container. " +
            "Always reply with outcome=COMPLETE and a one-line detail. " +
            "Do not run any tools. Do not edit files.");
        return (dir, promptFile);
    }

    private static void CleanupWorkspace(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task LiveDockerCodex_TrivialPrompt_ReturnsParseableOutcome()
    {
        if (!CliLiveTestGate.Enabled)
            return; // skip — set AIBOARD_TEST_LIVE_CLI=1 to enable

        if (!await IsDockerAvailableAsync())
            return; // skip — docker daemon not reachable

        const string image = "aiboard-codex-sandbox:latest";
        if (!await IsImageBuiltAsync(image))
            return; // skip — image not built (run scripts/build-codex-sandbox.ps1)

        if (!HasCodexAuth())
            return; // skip — no ~/.codex/ on host (run `codex login`)

        var (workspace, promptFile) = NewWorkspace();
        try
        {
            var options = Options.Create(new DockerCodexAgentOptions
            {
                ImageName = image,
                TimeoutSeconds = 180,
            });
            var mountBuilder = new DockerCodexMountBuilder(
                NullLogger<DockerCodexMountBuilder>.Instance);
            var executor = new DockerCodexAgentExecutor(
                options,
                TestTenant.Instance,
                NullLogger<DockerCodexAgentExecutor>.Instance,
                mountBuilder);

            var model = Environment.GetEnvironmentVariable("AIBOARD_TEST_LIVE_CODEX_MODEL")
                ?? "gpt-5.4-mini";

            var context = new AgentExecutionContext(
                TargetCardId: "0",
                TargetCardTitle: "Live smoke",
                WorkspacePath: workspace,
                TaskPrompt: "Respond with the structured outcome only — no analysis, no edits. " +
                            "Set outcome to COMPLETE and detail to 'docker-codex-live-ok'.",
                SystemPromptFilePath: promptFile,
                Model: model,
                ProviderParams: new Dictionary<string, string>
                {
                    // Belt-and-braces: even though the container is the security
                    // boundary, ask Codex to run read-only for this smoke test.
                    // Override the executor's default Yolo=true.
                    ["yolo"] = "false",
                    ["sandbox"] = "read-only",
                });

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(240));
            var result = await executor.ExecuteAsync(context, cts.Token);

            // Don't pin literal Detail — the model's wording will drift. We're
            // only validating that the docker-run + parse path produced SOME
            // structured outcome. A wire-shape regression manifests as
            // InvalidOperationException ("no structured_output"); a parser
            // miss would land here as a non-enum outcome value.
            Assert.True(
                result.Outcome is AgentOutcome.COMPLETE
                    or AgentOutcome.NEEDS_INFO
                    or AgentOutcome.ERROR,
                $"docker-codex returned an unexpected outcome: {result.Outcome}. " +
                $"Detail: {result.Detail}");
        }
        finally { CleanupWorkspace(workspace); }
    }
}
