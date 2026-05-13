using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

public class DockerCodexAgentExecutorTests
{
    private static DockerCodexAgentExecutor CreateExecutor(
        string imageName = "aiboard-codex-sandbox:latest",
        bool yolo = true,
        bool fullAuto = false,
        string? sandbox = null,
        Dictionary<string, DockerMount>? additionalMounts = null,
        string networkMode = "host",
        string? memoryLimit = null,
        string? cpuLimit = null,
        string containerUser = "",
        List<string>? groupAdd = null,
        bool mountHostDockerSocket = false,
        List<string>? performanceVolumes = null)
    {
        var opts = Options.Create(new DockerCodexAgentOptions
        {
            ImageName = imageName,
            ContainerNamePrefix = "aiboard-cdx",
            Yolo = yolo,
            FullAuto = fullAuto,
            Sandbox = sandbox,
            AdditionalMounts = additionalMounts ?? [],
            NetworkMode = networkMode,
            MemoryLimit = memoryLimit,
            CpuLimit = cpuLimit,
            ContainerUser = containerUser,
            GroupAdd = groupAdd ?? [],
            MountHostDockerSocket = mountHostDockerSocket,
            PerformanceVolumes = performanceVolumes ?? [],
        });
        return new DockerCodexAgentExecutor(
            opts,
            Helpers.TestTenant.Instance,
            NullLogger<DockerCodexAgentExecutor>.Instance);
    }

    private static AgentExecutionContext CreateContext(
        string? model = "gpt-5.4-mini",
        IReadOnlyDictionary<string, string>? providerParams = null)
    {
        return new AgentExecutionContext(
            TargetCardId: "42",
            TargetCardTitle: "Test",
            WorkspacePath: "/tmp/workspace",
            TaskPrompt: "implement the feature",
            SystemPromptFilePath: "/app/prompts/codex.md",
            Model: model,
            ProviderParams: providerParams);
    }

    private static DockerMountContext MountContext(string worktreePath = "/tmp/workspace") =>
        new(
            [
                new DockerMount
                {
                    HostPath = worktreePath,
                    ContainerPath = DockerMountBuilderBase.WorkspaceMountPoint,
                    ReadOnly = false,
                },
            ],
            new Dictionary<string, string>(),
            [(worktreePath, DockerMountBuilderBase.WorkspaceMountPoint)],
            []);

    // ── BuildCodexArgumentList ───────────────────────────────────────────────

    [Fact]
    public void BuildCodexArgumentList_StartsWithExec()
    {
        var executor = CreateExecutor();
        var args = executor.BuildCodexArgumentList(CreateContext(), "/tmp/codex-schema.json");

        Assert.Equal("exec", args[0]);
    }

    [Fact]
    public void BuildCodexArgumentList_WithModel_IncludesModelFlag()
    {
        var executor = CreateExecutor();
        var args = executor.BuildCodexArgumentList(CreateContext(model: "gpt-5.5"), "/tmp/s.json");

        var idx = Array.IndexOf(args, "--model");
        Assert.True(idx >= 0);
        Assert.Equal("gpt-5.5", args[idx + 1]);
    }

    [Fact]
    public void BuildCodexArgumentList_NoModel_OmitsModelFlag()
    {
        var executor = CreateExecutor();
        var args = executor.BuildCodexArgumentList(CreateContext(model: null), "/tmp/s.json");

        Assert.DoesNotContain("--model", args);
    }

    [Fact]
    public void BuildCodexArgumentList_AlwaysIncludesJsonAndOutputSchemaWithContainerPath()
    {
        var executor = CreateExecutor();
        var args = executor.BuildCodexArgumentList(CreateContext(), "/tmp/codex-schema.json");

        Assert.Contains("--json", args);
        var idx = Array.IndexOf(args, "--output-schema");
        Assert.True(idx >= 0);
        Assert.Equal("/tmp/codex-schema.json", args[idx + 1]);
    }

    [Fact]
    public void BuildCodexArgumentList_DefaultYoloTrue_IncludesYoloFlag()
    {
        var executor = CreateExecutor(yolo: true);
        var args = executor.BuildCodexArgumentList(CreateContext(), "/tmp/s.json");

        Assert.Contains("--yolo", args);
    }

    [Fact]
    public void BuildCodexArgumentList_YoloOmitsFullAutoAndSandbox()
    {
        var executor = CreateExecutor(yolo: true, fullAuto: true, sandbox: "workspace-write");
        var args = executor.BuildCodexArgumentList(CreateContext(), "/tmp/s.json");

        Assert.Contains("--yolo", args);
        Assert.DoesNotContain("--full-auto", args);
        Assert.DoesNotContain("--sandbox", args);
    }

    [Fact]
    public void BuildCodexArgumentList_ProviderParamsOverrideYolo_False()
    {
        var executor = CreateExecutor(yolo: true);
        var ctx = CreateContext(providerParams: new Dictionary<string, string>
        {
            ["yolo"] = "false",
            ["sandbox"] = "read-only",
        });
        var args = executor.BuildCodexArgumentList(ctx, "/tmp/s.json");

        Assert.DoesNotContain("--yolo", args);
        var idx = Array.IndexOf(args, "--sandbox");
        Assert.True(idx >= 0);
        Assert.Equal("read-only", args[idx + 1]);
    }

    [Fact]
    public void BuildCodexArgumentList_LastArgIsStdinDash()
    {
        var executor = CreateExecutor();
        var args = executor.BuildCodexArgumentList(CreateContext(), "/tmp/s.json");

        Assert.Equal("-", args[^1]);
    }

    // ── BuildContainerName ───────────────────────────────────────────────────

    [Fact]
    public void BuildContainerName_HasPrefixTenantCardSuffix()
    {
        var executor = CreateExecutor();
        var name = executor.BuildContainerName("42");

        Assert.StartsWith("aiboard-cdx-", name);
        Assert.Contains("-deadbeef-", name); // TestTenant.ShortHash
        Assert.Contains("-42-", name);
        // Total: aiboard-cdx-deadbeef-42-XXXXXXXX (8 hex)
        Assert.True(name.Length > "aiboard-cdx-deadbeef-42-".Length);
    }

    [Fact]
    public void BuildContainerName_TwoCallsProduceDifferentSuffixes()
    {
        var executor = CreateExecutor();
        var n1 = executor.BuildContainerName("42");
        var n2 = executor.BuildContainerName("42");

        Assert.NotEqual(n1, n2);
    }

    // ── BuildDockerArgumentList ──────────────────────────────────────────────

    [Fact]
    public void BuildDockerArgumentList_ContainsCoreDockerFlags()
    {
        var executor = CreateExecutor(imageName: "my-codex:v1");
        var args = executor.BuildDockerArgumentList(
            "aiboard-cdx-test", "/tmp/codex-schema.json", "/tmp/codex-schema.json",
            ["exec", "--json", "-"]);

        Assert.Contains("run", args);
        Assert.Contains("--rm", args);
        Assert.Contains("--init", args);
        Assert.Contains("-i", args);
        Assert.Contains("--name", args);
        Assert.Contains("aiboard-cdx-test", args);
        Assert.Contains("my-codex:v1", args);
        Assert.Contains("codex", args);
    }

    [Fact]
    public void BuildDockerArgumentList_IncludesTiniInitFlag()
    {
        var executor = CreateExecutor();
        var args = executor.BuildDockerArgumentList(
            "n", "/tmp/codex-schema.json", "/tmp/codex-schema.json", ["exec"]);

        Assert.Contains("--init", args);
    }

    [Fact]
    public void BuildDockerArgumentList_MountsSchemaFileReadOnly()
    {
        var executor = CreateExecutor();
        var args = executor.BuildDockerArgumentList(
            "n", "/tmp/host-schema.json", "/tmp/codex-schema.json", ["exec"]);

        // Look for the volume spec containing the schema mapping.
        var schemaMount = args.FirstOrDefault(a =>
            a.Contains("/tmp/host-schema.json") && a.Contains("/tmp/codex-schema.json"));
        Assert.NotNull(schemaMount);
        Assert.EndsWith(":ro", schemaMount);
    }

    [Fact]
    public void BuildDockerArgumentList_HostNetworkByDefault()
    {
        var executor = CreateExecutor(networkMode: "host");
        var args = executor.BuildDockerArgumentList(
            "n", "/tmp/host-schema.json", "/tmp/codex-schema.json", ["exec"]);

        var idx = Array.IndexOf(args, "--network");
        Assert.True(idx >= 0);
        Assert.Equal("host", args[idx + 1]);
    }

    [Fact]
    public void BuildDockerArgumentList_EmptyNetworkMode_OmitsNetworkFlag()
    {
        var executor = CreateExecutor(networkMode: "");
        var args = executor.BuildDockerArgumentList(
            "n", "/tmp/host-schema.json", "/tmp/codex-schema.json", ["exec"]);

        Assert.DoesNotContain("--network", args);
    }

    [Fact]
    public void BuildDockerArgumentList_MemoryAndCpuLimits_PassedThrough()
    {
        var executor = CreateExecutor(memoryLimit: "4g", cpuLimit: "2.0");
        var args = executor.BuildDockerArgumentList(
            "n", "/tmp/host-schema.json", "/tmp/codex-schema.json", ["exec"]);

        var memIdx = Array.IndexOf(args, "--memory");
        Assert.True(memIdx >= 0);
        Assert.Equal("4g", args[memIdx + 1]);
        var cpuIdx = Array.IndexOf(args, "--cpus");
        Assert.True(cpuIdx >= 0);
        Assert.Equal("2.0", args[cpuIdx + 1]);
    }

    [Fact]
    public void BuildDockerArgumentList_AdditionalMounts_Honoured()
    {
        var executor = CreateExecutor(additionalMounts: new()
        {
            ["extra-cache"] = new DockerMount
            {
                HostPath = "/var/cache/aiboard",
                ContainerPath = "/cache",
                ReadOnly = false,
            }
        });
        var args = executor.BuildDockerArgumentList(
            "n", "/tmp/host-schema.json", "/tmp/codex-schema.json", ["exec"]);

        Assert.Contains(args, a => a == "/var/cache/aiboard:/cache");
    }

    [Fact]
    public void BuildDockerArgumentList_PerformanceVolumes_ShadowWorkspaceMount()
    {
        const string worktree = "/tmp/aiboard/worktrees/52";
        var executor = CreateExecutor(performanceVolumes: ["node_modules", ".pnpm-store"]);
        var args = executor.BuildDockerArgumentList(
            "n", "/tmp/host-schema.json", "/tmp/codex-schema.json", ["exec"], MountContext(worktree));

        var volumeSpecs = args
            .Select((arg, idx) => (arg, idx))
            .Where(x => x.arg == "-v")
            .Select(x => args[x.idx + 1])
            .ToArray();
        var workspaceIdx = Array.IndexOf(volumeSpecs, $"{worktree}:{DockerMountBuilderBase.WorkspaceMountPoint}");
        var nodeVolume = $"{DockerMountBuilderBase.PerformanceVolumeName(worktree, "node_modules")}:/workspace/node_modules";
        var storeVolume = $"{DockerMountBuilderBase.PerformanceVolumeName(worktree, ".pnpm-store")}:/workspace/.pnpm-store";

        Assert.Contains(nodeVolume, volumeSpecs);
        Assert.Contains(storeVolume, volumeSpecs);
        Assert.True(Array.IndexOf(volumeSpecs, nodeVolume) > workspaceIdx);
        Assert.True(Array.IndexOf(volumeSpecs, storeVolume) > workspaceIdx);
    }

    [Fact]
    public void BuildDockerArgumentList_MountHostDockerSocket_AddsWritableSocketMount()
    {
        var executor = CreateExecutor(mountHostDockerSocket: true);
        var args = executor.BuildDockerArgumentList(
            "n", "/tmp/host-schema.json", "/tmp/codex-schema.json", ["exec"]);

        Assert.Contains("/var/run/docker.sock:/var/run/docker.sock", args);
        Assert.DoesNotContain("/var/run/docker.sock:/var/run/docker.sock:ro", args);
    }

    [Fact]
    public void BuildDockerArgumentList_GroupAdd_PassesGroupAddFlags()
    {
        var executor = CreateExecutor(groupAdd: ["998", "docker"]);
        var args = executor.BuildDockerArgumentList(
            "n", "/tmp/host-schema.json", "/tmp/codex-schema.json", ["exec"]);

        var indexes = args
            .Select((value, index) => (value, index))
            .Where(x => x.value == "--group-add")
            .Select(x => x.index)
            .ToArray();

        Assert.Equal(2, indexes.Length);
        Assert.Equal("998", args[indexes[0] + 1]);
        Assert.Equal("docker", args[indexes[1] + 1]);
    }

    // ── IsRateLimited ────────────────────────────────────────────────────────

    [Fact]
    public void IsRateLimited_DefaultPatternsHonoured()
    {
        var executor = CreateExecutor();
        Assert.True(executor.IsRateLimited("Error: rate limit exceeded"));
        Assert.True(executor.IsRateLimited("HTTP 429 too many requests"));
        Assert.False(executor.IsRateLimited("Some unrelated error"));
    }

    // ── IsDockerExitCode ─────────────────────────────────────────────────────

    [Theory]
    [InlineData(125, true)]
    [InlineData(126, true)]
    [InlineData(127, true)]
    [InlineData(137, true)]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(124, false)]
    public void IsDockerExitCode_ClassifiesCorrectly(int exitCode, bool expected)
    {
        Assert.Equal(expected, DockerCodexAgentExecutor.IsDockerExitCode(exitCode));
    }
}
