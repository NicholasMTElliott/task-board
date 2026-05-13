using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

public class DockerPerformanceVolumeManagerTests
{
    [Fact]
    public void PerformanceVolumeName_IsStable()
    {
        var first = DockerMountBuilderBase.PerformanceVolumeName(
            @"C:\Repos\Eve-worktrees\aiboard\50", "node_modules");
        var second = DockerMountBuilderBase.PerformanceVolumeName(
            @"C:\Repos\Eve-worktrees\aiboard\50", "node_modules");

        Assert.Equal(first, second);
    }

    [Fact]
    public void PerformanceVolumeName_DistinctWorktreesDiffer()
    {
        var first = DockerMountBuilderBase.PerformanceVolumeName("/repos/eve-worktrees/aiboard/50", "node_modules");
        var second = DockerMountBuilderBase.PerformanceVolumeName("/repos/eve-worktrees/aiboard/52", "node_modules");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void PerformanceVolumeName_MatchesDockerVolumeNameShape()
    {
        var name = DockerMountBuilderBase.PerformanceVolumeName("/repos/eve", ".pnpm-store");

        Assert.Matches(@"^[a-zA-Z0-9][a-zA-Z0-9_.-]+$", name);
    }

    [Fact]
    public void PerformanceVolumeName_WindowsPathCaseInsensitive()
    {
        var first = DockerMountBuilderBase.PerformanceVolumeName(@"C:\Foo\Bar", "node_modules");
        var second = DockerMountBuilderBase.PerformanceVolumeName(@"c:\foo\bar", "node_modules");

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("")]
    [InlineData("/absolute")]
    [InlineData("C:/absolute")]
    [InlineData("../cache")]
    [InlineData("cache/../other")]
    [InlineData(".git")]
    [InlineData(".git/hooks")]
    public void NormalizePerformanceVolumePath_RejectsUnsafePaths(string value)
    {
        Assert.Throws<ArgumentException>(() =>
            DockerMountBuilderBase.NormalizePerformanceVolumePath(value));
    }

    [Fact]
    public async Task EnsureAsync_CreatesAndInitializesEachVolume()
    {
        var calls = new List<string[]>();
        ProcessRunnerDelegate runner = (_, args, _, _, _, _, _, _, _) =>
        {
            calls.Add(args);
            return Task.FromResult((0, "", ""));
        };
        var options = new DockerClaudeAgentOptions
        {
            ImageName = "aiboard-test:latest",
            PerformanceVolumes = ["node_modules", ".pnpm-store"],
        };

        await DockerPerformanceVolumeManager.EnsureAsync(
            options, "/repos/eve-worktrees/aiboard/50", runner,
            NullLogger.Instance, CancellationToken.None);

        Assert.Equal(4, calls.Count);
        Assert.Equal(["volume", "create", "--label", "aiboard-perf=1"], calls[0][..4]);
        Assert.Equal(["run", "--rm", "-u", "0"], calls[1][..4]);
        Assert.Contains("aiboard-test:latest", calls[1]);
        Assert.Contains("chown -R agent:agent /init", string.Join(" ", calls[1]));
    }
}
