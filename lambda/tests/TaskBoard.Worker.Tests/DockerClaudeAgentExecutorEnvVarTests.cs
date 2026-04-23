using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

/// <summary>
/// Tests that <see cref="DockerClaudeAgentExecutor.BuildDockerArgumentList"/> emits
/// <c>-e KEY=VALUE</c> for every environment variable in the supplied
/// <see cref="DockerMountContext"/>. Gaps in this path leak through to the
/// container and quietly break features that depend on env injection
/// (e.g. <c>GIT_OPTIONAL_LOCKS=0</c>).
/// </summary>
public class DockerClaudeAgentExecutorEnvVarTests
{
    private static DockerClaudeAgentExecutor CreateExecutor() =>
        new(
            Options.Create(new DockerClaudeAgentOptions { ImageName = "test:latest" }),
            Helpers.TestTenant.Instance,
            NullLogger<DockerClaudeAgentExecutor>.Instance);

    private static DockerMountContext MakeMountContext(
        IReadOnlyDictionary<string, string> envVars)
    {
        return new DockerMountContext(
            mounts: Array.Empty<DockerMount>(),
            environmentVariables: envVars,
            pathMap: Array.Empty<(string, string)>(),
            tempFiles: new List<string>(),
            tempDirs: new List<string>());
    }

    [Fact]
    public void BuildDockerArgumentList_MountContextEnvVar_EmitsDashE()
    {
        var executor = CreateExecutor();
        var ctx = MakeMountContext(new Dictionary<string, string>
        {
            ["GIT_OPTIONAL_LOCKS"] = "0",
        });

        var args = executor.BuildDockerArgumentList(
            "container1", "/host/prompts", ["--print"], ctx);

        var idx = Array.IndexOf(args, "-e");
        Assert.True(idx >= 0, "Expected -e flag for env var injection");
        Assert.Equal("GIT_OPTIONAL_LOCKS=0", args[idx + 1]);
    }

    [Fact]
    public void BuildDockerArgumentList_MultipleEnvVars_EmitsEachWithDashE()
    {
        var executor = CreateExecutor();
        var ctx = MakeMountContext(new Dictionary<string, string>
        {
            ["GIT_OPTIONAL_LOCKS"] = "0",
            ["LOG_LEVEL"] = "debug",
        });

        var args = executor.BuildDockerArgumentList(
            "container1", "/host/prompts", ["--print"], ctx);

        var envPairs = args
            .Select((a, i) => (arg: a, idx: i))
            .Where(x => x.arg == "-e")
            .Select(x => args[x.idx + 1])
            .ToList();

        Assert.Equal(2, envPairs.Count);
        Assert.Contains("GIT_OPTIONAL_LOCKS=0", envPairs);
        Assert.Contains("LOG_LEVEL=debug", envPairs);
    }

    [Fact]
    public void BuildDockerArgumentList_EmptyEnvVars_NoEFlag()
    {
        var executor = CreateExecutor();
        var ctx = MakeMountContext(new Dictionary<string, string>());

        var args = executor.BuildDockerArgumentList(
            "container1", "/host/prompts", ["--print"], ctx);

        Assert.DoesNotContain("-e", args);
    }

    [Fact]
    public void BuildDockerArgumentList_NoMountContext_NoEFlag()
    {
        // Without a mount context, no env vars are emitted regardless of defaults.
        var executor = CreateExecutor();

        var args = executor.BuildDockerArgumentList(
            "container1", "/host/prompts", ["--print"]);

        Assert.DoesNotContain("-e", args);
    }

    [Fact]
    public void BuildDockerArgumentList_EnvVarWithSpecialChars_PreservedLiterally()
    {
        // Values may contain '=', spaces, or other characters. The builder must
        // pass the VALUE portion verbatim — docker handles it from there.
        var executor = CreateExecutor();
        var ctx = MakeMountContext(new Dictionary<string, string>
        {
            ["KEY"] = "v=alue with spaces",
        });

        var args = executor.BuildDockerArgumentList(
            "container1", "/host/prompts", ["--print"], ctx);

        var idx = Array.IndexOf(args, "-e");
        Assert.Equal("KEY=v=alue with spaces", args[idx + 1]);
    }
}
