using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

/// <summary>
/// Tests specific to <see cref="DockerOpenCodeMountBuilder"/>. The shared
/// worktree/<c>.git</c> mount logic is already covered by
/// <see cref="DockerClaudeMountBuilderTests"/> (it lives on the
/// <see cref="DockerMountBuilderBase"/> base class). What's distinct for
/// OpenCode and tested here:
/// <list type="bullet">
///   <item>No credential mount (the local llama.cpp server uses a dummy token,
///         so there is nothing to stage from the host filesystem).</item>
///   <item>Connection env vars (<c>OPENCODE_PROVIDER_BASE_URL</c>,
///         <c>OPENCODE_AUTH_TOKEN</c>, <c>OPENCODE_MODEL_NAME</c>) populated
///         from options and forwarded through
///         <see cref="DockerMountContext.EnvironmentVariables"/>.</item>
///   <item><c>GIT_OPTIONAL_LOCKS=0</c> preserved alongside the OpenCode vars.</item>
/// </list>
/// </summary>
public class DockerOpenCodeMountBuilderTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly DockerOpenCodeMountBuilder Builder =
        new(NullLogger<DockerOpenCodeMountBuilder>.Instance);

    public DockerOpenCodeMountBuilderTests()
    {
        _tempDir = Path.Combine(
            Path.GetTempPath(),
            "oc-mount-builder-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task BuildAsync_NoCredentialMount_OnlyWorkspaceAndGitMounts()
    {
        // Unlike DockerClaudeMountBuilder, the OpenCode builder does not stage
        // a credential directory. Mount count must be ≤ 3 (worktree, base .git,
        // .git override) — never 4.
        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerOpenCodeAgentOptions(),
            cancellationToken: CancellationToken.None);

        Assert.True(ctx.Mounts.Count <= 3,
            $"Expected ≤3 mounts (workspace, base .git, .git override), got {ctx.Mounts.Count}");

        // None of them should target a credential mount point.
        Assert.DoesNotContain(ctx.Mounts,
            m => m.ContainerPath.Contains("/.claude", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ctx.Mounts,
            m => m.ContainerPath.Contains("/.opencode", StringComparison.OrdinalIgnoreCase)
              && !m.ContainerPath.StartsWith("/workspace"));
    }

    [Fact]
    public async Task BuildAsync_AlwaysIncludesWorkspaceMountAtSlashWorkspace()
    {
        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerOpenCodeAgentOptions(),
            cancellationToken: CancellationToken.None);

        var workspace = ctx.Mounts.FirstOrDefault(
            m => m.ContainerPath == DockerMountBuilderBase.WorkspaceMountPoint);
        Assert.NotNull(workspace);
        Assert.False(workspace.ReadOnly);
    }

    [Fact]
    public async Task BuildAsync_EnvVars_IncludeOpenCodeConnectionTriple()
    {
        // The three env vars consumed by the sandbox entrypoint must always
        // appear in the resulting context. If any is missing, the entrypoint's
        // ${VAR:?} parameter expansion will abort the container at startup.
        var options = new DockerOpenCodeAgentOptions
        {
            ProviderBaseUrl = "http://my-llama:9090",
            AuthToken = "test-token",
            ModelName = "test-model",
        };

        await using var ctx = await Builder.BuildAsync(
            _tempDir, options, cancellationToken: CancellationToken.None);

        Assert.Equal("http://my-llama:9090",
            ctx.EnvironmentVariables["OPENCODE_PROVIDER_BASE_URL"]);
        Assert.Equal("test-token",
            ctx.EnvironmentVariables["OPENCODE_AUTH_TOKEN"]);
        Assert.Equal("test-model",
            ctx.EnvironmentVariables["OPENCODE_MODEL_NAME"]);
    }

    [Fact]
    public async Task BuildAsync_ModelOverride_OverridesOptionsModelName()
    {
        // The per-call modelOverride lets a workflow role pick between the
        // no-think and -think Qwen variants without editing the options
        // default. Empty/whitespace must fall back to the options value.
        var options = new DockerOpenCodeAgentOptions
        {
            ModelName = "qwen3.6-35b-a3b",
        };

        await using var withOverride = await Builder.BuildAsync(
            _tempDir, options,
            modelOverride: "qwen3.6-35b-a3b-think",
            cancellationToken: CancellationToken.None);
        Assert.Equal("qwen3.6-35b-a3b-think",
            withOverride.EnvironmentVariables["OPENCODE_MODEL_NAME"]);

        await using var withoutOverride = await Builder.BuildAsync(
            _tempDir, options,
            modelOverride: null,
            cancellationToken: CancellationToken.None);
        Assert.Equal("qwen3.6-35b-a3b",
            withoutOverride.EnvironmentVariables["OPENCODE_MODEL_NAME"]);

        await using var withWhitespace = await Builder.BuildAsync(
            _tempDir, options,
            modelOverride: "   ",
            cancellationToken: CancellationToken.None);
        Assert.Equal("qwen3.6-35b-a3b",
            withWhitespace.EnvironmentVariables["OPENCODE_MODEL_NAME"]);
    }

    [Fact]
    public async Task BuildAsync_EnvVars_PreserveGitOptionalLocks()
    {
        // The base-class GIT_OPTIONAL_LOCKS=0 must survive when the OpenCode
        // builder layers on its connection vars. Regression guard against an
        // accidental dictionary-replacement that would remove it.
        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerOpenCodeAgentOptions(),
            cancellationToken: CancellationToken.None);

        Assert.Equal("0", ctx.EnvironmentVariables["GIT_OPTIONAL_LOCKS"]);
    }

    [Fact]
    public async Task BuildAsync_DefaultOptions_UsesDocumentedDefaults()
    {
        // Sanity check: the env vars seeded into the context come from the
        // option defaults documented in docs/OpenCodeSandbox.md. If a default
        // is changed, this fails so the docs are updated in the same PR.
        await using var ctx = await Builder.BuildAsync(
            _tempDir,
            new DockerOpenCodeAgentOptions(),
            cancellationToken: CancellationToken.None);

        // Default URL carries the /v1 suffix that the @ai-sdk/openai-compatible
        // adapter expects. The default model is the no-think Qwen alias —
        // production default per local-llm/Qwen-3.6.md.
        Assert.Equal("http://llama-server:8080/v1",
            ctx.EnvironmentVariables["OPENCODE_PROVIDER_BASE_URL"]);
        Assert.Equal("local",
            ctx.EnvironmentVariables["OPENCODE_AUTH_TOKEN"]);
        Assert.Equal("qwen3.6-35b-a3b",
            ctx.EnvironmentVariables["OPENCODE_MODEL_NAME"]);
    }
}
