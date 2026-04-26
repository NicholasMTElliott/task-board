using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

/// <summary>
/// Tests specific to <see cref="DockerClaudeQwenMountBuilder"/>. The shared
/// worktree/<c>.git</c> mount logic is already covered by
/// <see cref="DockerClaudeMountBuilderTests"/> (it lives on
/// <see cref="DockerMountBuilderBase"/>). What's distinct here:
/// <list type="bullet">
///   <item>No host credential mount — instead, a synthetic <c>~/.claude</c>
///         dir staged in a temp dir with <c>settings.json</c> and a dummy
///         <c>credentials.json</c>.</item>
///   <item>Anthropic redirect env vars (<c>ANTHROPIC_BASE_URL</c>,
///         <c>ANTHROPIC_AUTH_TOKEN</c>, <c>ANTHROPIC_API_KEY</c>,
///         <c>ANTHROPIC_MODEL</c>) populated from options + per-call override.</item>
///   <item>Cache-friendliness toggles (<c>CLAUDE_CODE_ATTRIBUTION_HEADER</c>,
///         <c>CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC</c>) emitted only when
///         the corresponding option flag is true.</item>
/// </list>
/// </summary>
public class DockerClaudeQwenMountBuilderTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly DockerClaudeQwenMountBuilder Builder =
        new(NullLogger<DockerClaudeQwenMountBuilder>.Instance);

    public DockerClaudeQwenMountBuilderTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "cq-mount-builder-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task BuildAsync_NoHostCredentialDirReferenced_OnlySyntheticClaudeMount()
    {
        // Critical: the Qwen-target builder must NOT mount the operator's real
        // ~/.claude — that would risk leaking real Anthropic credentials into
        // a Qwen-target run. The only /home/agent/.claude mount must be the
        // synthetic temp dir.
        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeQwenAgentOptions(),
            cancellationToken: CancellationToken.None);

        var claudeMounts = ctx.Mounts
            .Where(m => m.ContainerPath == DockerClaudeQwenMountBuilder.ClaudeConfigMountPoint)
            .ToList();

        Assert.Single(claudeMounts);
        // The host path must be inside the OS temp directory — that's the
        // synthetic staging dir owned by the mount context (cleaned up on
        // disposal). Anything else (most importantly the user's real
        // ~/.claude) would leak Anthropic credentials into a Qwen-target run.
        var tempDirRoot = Path.GetTempPath().Replace('\\', '/').TrimEnd('/');
        Assert.StartsWith(tempDirRoot, claudeMounts[0].HostPath,
            StringComparison.OrdinalIgnoreCase);

        // And the directory leaf must look like one of OUR staging dirs — the
        // builder names them aiboard-cq-claude-{guid}.
        Assert.Contains("aiboard-cq-claude-", claudeMounts[0].HostPath,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildAsync_StagesSettingsAndCredentialsInsideClaudeMount()
    {
        // Both files must exist: settings.json bypasses onboarding,
        // credentials.json silences the CLI's startup credential probe.
        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeQwenAgentOptions(),
            cancellationToken: CancellationToken.None);

        var claudeMount = ctx.Mounts.Single(
            m => m.ContainerPath == DockerClaudeQwenMountBuilder.ClaudeConfigMountPoint);

        // Mount HostPath uses forward slashes for Docker; convert back for filesystem ops.
        var hostDir = claudeMount.HostPath.Replace('/', Path.DirectorySeparatorChar);

        Assert.True(File.Exists(Path.Combine(hostDir, "settings.json")),
            "Synthetic settings.json missing");
        Assert.True(File.Exists(Path.Combine(hostDir, "credentials.json")),
            "Synthetic credentials.json missing");

        var settingsJson = await File.ReadAllTextAsync(Path.Combine(hostDir, "settings.json"));
        using var doc = JsonDocument.Parse(settingsJson);
        Assert.True(
            doc.RootElement.GetProperty("hasCompletedOnboarding").GetBoolean(),
            "settings.json must set hasCompletedOnboarding=true to bypass CLI onboarding");
    }

    [Fact]
    public async Task BuildAsync_RedirectEnvVars_PointAtConfiguredProxy()
    {
        var options = new DockerClaudeQwenAgentOptions
        {
            ProviderBaseUrl = "http://my-llama:9090",
            AuthToken = "test-token",
            ModelName = "test-model",
        };

        await using var ctx = await Builder.BuildAsync(
            _tempDir, options, cancellationToken: CancellationToken.None);

        Assert.Equal("http://my-llama:9090",
            ctx.EnvironmentVariables["ANTHROPIC_BASE_URL"]);
        Assert.Equal("test-token",
            ctx.EnvironmentVariables["ANTHROPIC_AUTH_TOKEN"]);
        // Both names set — different Claude CLI versions check different env names.
        Assert.Equal("test-token",
            ctx.EnvironmentVariables["ANTHROPIC_API_KEY"]);
        Assert.Equal("test-model",
            ctx.EnvironmentVariables["ANTHROPIC_MODEL"]);
    }

    [Fact]
    public async Task BuildAsync_ModelOverride_OverridesOptionsModelName()
    {
        // The per-call modelOverride is how a workflow role picks between
        // qwen3.6-35b-a3b and qwen3.6-35b-a3b-think without editing the
        // options default.
        var options = new DockerClaudeQwenAgentOptions
        {
            ModelName = "qwen3.6-35b-a3b",
        };

        await using var withOverride = await Builder.BuildAsync(
            _tempDir, options,
            modelOverride: "qwen3.6-35b-a3b-think",
            cancellationToken: CancellationToken.None);
        Assert.Equal("qwen3.6-35b-a3b-think",
            withOverride.EnvironmentVariables["ANTHROPIC_MODEL"]);

        await using var withoutOverride = await Builder.BuildAsync(
            _tempDir, options,
            modelOverride: null,
            cancellationToken: CancellationToken.None);
        Assert.Equal("qwen3.6-35b-a3b",
            withoutOverride.EnvironmentVariables["ANTHROPIC_MODEL"]);

        await using var withWhitespace = await Builder.BuildAsync(
            _tempDir, options,
            modelOverride: "   ",
            cancellationToken: CancellationToken.None);
        Assert.Equal("qwen3.6-35b-a3b",
            withWhitespace.EnvironmentVariables["ANTHROPIC_MODEL"]);
    }

    [Fact]
    public async Task BuildAsync_CacheFriendlinessFlags_GatedByOptions()
    {
        // Both flags default to true (recommended per Qwen-3.6.md).
        await using var defaults = await Builder.BuildAsync(
            _tempDir, new DockerClaudeQwenAgentOptions(),
            cancellationToken: CancellationToken.None);
        Assert.Equal("0",
            defaults.EnvironmentVariables["CLAUDE_CODE_ATTRIBUTION_HEADER"]);
        Assert.Equal("1",
            defaults.EnvironmentVariables["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"]);

        // Disabling the toggles must remove the env vars entirely (not set them
        // to "1"/"0" inverted) so the CLI sees its own default behaviour.
        await using var off = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeQwenAgentOptions
            {
                DisableAttributionHeader = false,
                DisableNonessentialTraffic = false,
            },
            cancellationToken: CancellationToken.None);
        Assert.False(off.EnvironmentVariables.ContainsKey("CLAUDE_CODE_ATTRIBUTION_HEADER"));
        Assert.False(off.EnvironmentVariables.ContainsKey("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"));
    }

    [Fact]
    public async Task BuildAsync_EnvVars_PreserveGitOptionalLocks()
    {
        // The base-class GIT_OPTIONAL_LOCKS=0 must survive when the Qwen
        // builder layers on its env vars. Regression guard against an
        // accidental dictionary-replacement that would remove it.
        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeQwenAgentOptions(),
            cancellationToken: CancellationToken.None);

        Assert.Equal("0", ctx.EnvironmentVariables["GIT_OPTIONAL_LOCKS"]);
    }

    [Fact]
    public async Task BuildAsync_DefaultOptions_UsesDocumentedDefaults()
    {
        // Sanity check: defaults match what's documented in
        // docs/ClaudeQwenSandbox.md and the Qwen-3.6.md model card. If a
        // default changes, this fails so the docs get updated alongside.
        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerClaudeQwenAgentOptions(),
            cancellationToken: CancellationToken.None);

        Assert.Equal("http://llama-server:8080",
            ctx.EnvironmentVariables["ANTHROPIC_BASE_URL"]);
        Assert.Equal("local",
            ctx.EnvironmentVariables["ANTHROPIC_AUTH_TOKEN"]);
        Assert.Equal("qwen3.6-35b-a3b",
            ctx.EnvironmentVariables["ANTHROPIC_MODEL"]);
    }
}
