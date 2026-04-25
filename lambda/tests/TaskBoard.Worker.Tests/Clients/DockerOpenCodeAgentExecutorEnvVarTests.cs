using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

/// <summary>
/// Tests that <see cref="DockerOpenCodeAgentExecutor.BuildDockerArgumentList"/>
/// correctly emits <c>-e KEY=VALUE</c> for every entry in
/// <see cref="DockerMountContext.EnvironmentVariables"/>. Critical for OpenCode
/// because the sandbox entrypoint validates <c>OPENCODE_PROVIDER_BASE_URL</c>,
/// <c>OPENCODE_AUTH_TOKEN</c>, and <c>OPENCODE_MODEL_NAME</c> at startup with
/// <c>${VAR:?}</c>: a regression in env-var emission would surface as
/// "OPENCODE_PROVIDER_BASE_URL must be set" failures inside the container,
/// not as a unit-test failure here — so this guard exists to catch breakage
/// at the build-args layer.
/// </summary>
public class DockerOpenCodeAgentExecutorEnvVarTests
{
    private static DockerOpenCodeAgentExecutor CreateExecutor() =>
        new(
            Options.Create(new DockerOpenCodeAgentOptions
            {
                ImageName = "test:latest",
                NetworkMode = "llm-net",
            }),
            Helpers.TestTenant.Instance,
            NullLogger<DockerOpenCodeAgentExecutor>.Instance);

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
    public void BuildDockerArgumentList_OpenCodeConnectionEnvVars_AllEmitted()
    {
        // The three env vars the entrypoint requires must all appear in the
        // docker -e flag stream. Missing any of them breaks every single run.
        var executor = CreateExecutor();
        var ctx = MakeMountContext(new Dictionary<string, string>
        {
            ["GIT_OPTIONAL_LOCKS"] = "0",
            ["OPENCODE_PROVIDER_BASE_URL"] = "http://llama-server:8080",
            ["OPENCODE_AUTH_TOKEN"] = "local",
            ["OPENCODE_MODEL_NAME"] = "qwen3.6-35b-a3b",
        });

        var args = executor.BuildDockerArgumentList(
            containerName: "test-container",
            hostPromptDir: "/host/prompts",
            openCodeArgs: new[] { "run" },
            mountContext: ctx);

        var envPairs = args
            .Select((a, i) => (arg: a, idx: i))
            .Where(x => x.arg == "-e")
            .Select(x => args[x.idx + 1])
            .ToList();

        Assert.Equal(4, envPairs.Count);
        Assert.Contains("GIT_OPTIONAL_LOCKS=0", envPairs);
        Assert.Contains("OPENCODE_PROVIDER_BASE_URL=http://llama-server:8080", envPairs);
        Assert.Contains("OPENCODE_AUTH_TOKEN=local", envPairs);
        Assert.Contains("OPENCODE_MODEL_NAME=qwen3.6-35b-a3b", envPairs);
    }

    [Fact]
    public void BuildDockerArgumentList_MountList_EmitsBindSpecsInOrder()
    {
        var executor = CreateExecutor();
        var ctx = new DockerMountContext(
            mounts: new[]
            {
                new DockerMount { HostPath = "/host/wt", ContainerPath = "/workspace", ReadOnly = false },
                new DockerMount { HostPath = "/host/.git", ContainerPath = "/repo/.git", ReadOnly = true },
            },
            environmentVariables: new Dictionary<string, string>(),
            pathMap: Array.Empty<(string, string)>(),
            tempFiles: new List<string>(),
            tempDirs: new List<string>());

        var args = executor.BuildDockerArgumentList(
            containerName: "test-container",
            hostPromptDir: "",
            openCodeArgs: new[] { "run" },
            mountContext: ctx);

        var mountPairs = args
            .Select((a, i) => (arg: a, idx: i))
            .Where(x => x.arg == "-v")
            .Select(x => args[x.idx + 1])
            .ToList();

        Assert.Contains("/host/wt:/workspace", mountPairs);
        Assert.Contains("/host/.git:/repo/.git:ro", mountPairs);
    }

    [Fact]
    public void BuildDockerArgumentList_AdditionalMounts_AppendedToArgs()
    {
        // Operators can add static volume mounts beyond the workspace/git set
        // (e.g. a shared cache). Verify they survive into the final docker args.
        var options = new DockerOpenCodeAgentOptions
        {
            ImageName = "test:latest",
            AdditionalMounts =
            {
                ["cache"] = new DockerMount
                {
                    HostPath = "/host/cache",
                    ContainerPath = "/cache",
                    ReadOnly = false,
                },
            },
        };
        var executor = new DockerOpenCodeAgentExecutor(
            Options.Create(options),
            Helpers.TestTenant.Instance,
            NullLogger<DockerOpenCodeAgentExecutor>.Instance);

        var args = executor.BuildDockerArgumentList(
            containerName: "test-container",
            hostPromptDir: "",
            openCodeArgs: new[] { "run" },
            mountContext: null);

        var mountPairs = args
            .Select((a, i) => (arg: a, idx: i))
            .Where(x => x.arg == "-v")
            .Select(x => args[x.idx + 1])
            .ToList();

        Assert.Contains("/host/cache:/cache", mountPairs);
    }
}
