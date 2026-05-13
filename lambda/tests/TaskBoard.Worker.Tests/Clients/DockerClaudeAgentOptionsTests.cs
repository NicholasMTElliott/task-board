using Microsoft.Extensions.Configuration;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

public class DockerClaudeAgentOptionsTests
{
    [Fact]
    public void DockerClaudeAgentOptions_Defaults_AreCorrect()
    {
        var opts = new DockerClaudeAgentOptions();

        Assert.Equal("aiboard-agent-sandbox:latest", opts.ImageName);
        Assert.Equal("host", opts.NetworkMode);
        Assert.Equal("", opts.ContainerUser);
        Assert.Null(opts.MemoryLimit);
        Assert.Null(opts.CpuLimit);
        Assert.Empty(opts.GroupAdd);
        Assert.Null(opts.CredentialPath);
        Assert.True(opts.ReuseContainer);
        Assert.Equal("aiboard-run", opts.ContainerNamePrefix);
        Assert.Equal("/mnt/aiboard/prompts", opts.PromptMountPoint);
        Assert.Equal(10.00m, opts.MaxBudgetUsd);
        Assert.Equal(7200, opts.TimeoutSeconds);
        Assert.Equal(1200, opts.InactivityTimeoutSeconds);
        Assert.False(opts.MountHostDockerSocket);
        Assert.Equal("/var/run/docker.sock", opts.HostDockerSocketPath);
        Assert.Equal("/var/run/docker.sock", opts.ContainerDockerSocketPath);
        Assert.Empty(opts.AdditionalMounts);
        Assert.Empty(opts.PerformanceVolumes);
        Assert.Equal("agent:agent", opts.PerformanceVolumeOwner);
    }

    [Fact]
    public void DockerClaudeAgentOptions_SectionName_IsDockerAgentsClaude()
    {
        Assert.Equal("DockerAgents:Claude", DockerClaudeAgentOptions.SectionName);
    }

    [Fact]
    public void DockerClaudeAgentOptions_LegacySectionName_IsDocker()
    {
        Assert.Equal("Docker", DockerClaudeAgentOptions.LegacySectionName);
    }

    [Fact]
    public void DockerClaudeAgentOptions_BindsAllScalarPropertiesFromConfiguration()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["DockerAgents:Claude:ImageName"] = "custom-sandbox:2.0",
            ["DockerAgents:Claude:NetworkMode"] = "none",
            ["DockerAgents:Claude:MemoryLimit"] = "4g",
            ["DockerAgents:Claude:CpuLimit"] = "2.0",
            ["DockerAgents:Claude:ContainerUser"] = "agent",
            ["DockerAgents:Claude:GroupAdd:0"] = "998",
            ["DockerAgents:Claude:GroupAdd:1"] = "docker",
            ["DockerAgents:Claude:MountHostDockerSocket"] = "true",
            ["DockerAgents:Claude:HostDockerSocketPath"] = "/custom/docker.sock",
            ["DockerAgents:Claude:ContainerDockerSocketPath"] = "/run/docker.sock",
            ["DockerAgents:Claude:CredentialPath"] = "/home/user/.claude",
            ["DockerAgents:Claude:PerformanceVolumeOwner"] = "1001:1001",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var opts = new DockerClaudeAgentOptions();
        config.GetSection(DockerClaudeAgentOptions.SectionName).Bind(opts);

        Assert.Equal("custom-sandbox:2.0", opts.ImageName);
        Assert.Equal("none", opts.NetworkMode);
        Assert.Equal("4g", opts.MemoryLimit);
        Assert.Equal("2.0", opts.CpuLimit);
        Assert.Equal("agent", opts.ContainerUser);
        Assert.Equal(["998", "docker"], opts.GroupAdd);
        Assert.True(opts.MountHostDockerSocket);
        Assert.Equal("/custom/docker.sock", opts.HostDockerSocketPath);
        Assert.Equal("/run/docker.sock", opts.ContainerDockerSocketPath);
        Assert.Equal("/home/user/.claude", opts.CredentialPath);
        Assert.Equal("1001:1001", opts.PerformanceVolumeOwner);
    }

    [Fact]
    public void DockerClaudeAgentOptions_PerformanceVolumes_BindsListFromConfiguration()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["DockerAgents:Claude:PerformanceVolumes:0"] = "node_modules",
            ["DockerAgents:Claude:PerformanceVolumes:1"] = ".pnpm-store",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var opts = new DockerClaudeAgentOptions();
        config.GetSection(DockerClaudeAgentOptions.SectionName).Bind(opts);

        Assert.Equal(["node_modules", ".pnpm-store"], opts.PerformanceVolumes);
    }

    [Fact]
    public void DockerClaudeAgentOptions_AdditionalMounts_BindsDictionaryFromConfiguration()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["DockerAgents:Claude:AdditionalMounts:workspace:HostPath"] = "/host/data",
            ["DockerAgents:Claude:AdditionalMounts:workspace:ContainerPath"] = "/container/data",
            ["DockerAgents:Claude:AdditionalMounts:workspace:ReadOnly"] = "false",
            ["DockerAgents:Claude:AdditionalMounts:tools:HostPath"] = "/host/tools",
            ["DockerAgents:Claude:AdditionalMounts:tools:ContainerPath"] = "/container/tools",
            ["DockerAgents:Claude:AdditionalMounts:tools:ReadOnly"] = "true",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var opts = new DockerClaudeAgentOptions();
        config.GetSection(DockerClaudeAgentOptions.SectionName).Bind(opts);

        Assert.Equal(2, opts.AdditionalMounts.Count);
        Assert.True(opts.AdditionalMounts.ContainsKey("workspace"));
        Assert.Equal("/host/data", opts.AdditionalMounts["workspace"].HostPath);
        Assert.Equal("/container/data", opts.AdditionalMounts["workspace"].ContainerPath);
        Assert.False(opts.AdditionalMounts["workspace"].ReadOnly);
        Assert.True(opts.AdditionalMounts.ContainsKey("tools"));
        Assert.Equal("/host/tools", opts.AdditionalMounts["tools"].HostPath);
        Assert.True(opts.AdditionalMounts["tools"].ReadOnly);
    }

    [Fact]
    public void DockerClaudeAgentOptions_EmptyAdditionalMounts_RemainsEmpty()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["DockerAgents:Claude:ImageName"] = "myimage:latest",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var opts = new DockerClaudeAgentOptions();
        config.GetSection(DockerClaudeAgentOptions.SectionName).Bind(opts);

        Assert.Empty(opts.AdditionalMounts);
    }

    [Fact]
    public void DockerClaudeAgentOptions_NullOptionalProperties_RemainsNullWhenNotSet()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["DockerAgents:Claude:ImageName"] = "myimage:latest",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var opts = new DockerClaudeAgentOptions();
        config.GetSection(DockerClaudeAgentOptions.SectionName).Bind(opts);

        Assert.Null(opts.MemoryLimit);
        Assert.Null(opts.CpuLimit);
    }

    [Fact]
    public void DockerClaudeAgentOptions_PartialConfig_PreservesDefaults()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["DockerAgents:Claude:NetworkMode"] = "none",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var opts = new DockerClaudeAgentOptions();
        config.GetSection(DockerClaudeAgentOptions.SectionName).Bind(opts);

        Assert.Equal("aiboard-agent-sandbox:latest", opts.ImageName);
        Assert.Equal("none", opts.NetworkMode);
    }

    [Fact]
    public void DockerClaudeAgentOptions_LegacyDockerSection_StillBinds()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["Docker:ImageName"] = "legacy-sandbox:1.0",
            ["Docker:NetworkMode"] = "none",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var opts = new DockerClaudeAgentOptions();
        config.GetSection(DockerClaudeAgentOptions.LegacySectionName).Bind(opts);

        Assert.Equal("legacy-sandbox:1.0", opts.ImageName);
        Assert.Equal("none", opts.NetworkMode);
    }

    [Fact]
    public void DockerClaudeAgentOptions_NewSectionOverridesLegacy()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["Docker:ImageName"] = "legacy-sandbox:1.0",
            ["Docker:NetworkMode"] = "legacy-network",
            ["DockerAgents:Claude:ImageName"] = "new-sandbox:2.0",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var opts = new DockerClaudeAgentOptions();
        config.GetSection(DockerClaudeAgentOptions.LegacySectionName).Bind(opts);
        config.GetSection(DockerClaudeAgentOptions.SectionName).Bind(opts);

        Assert.Equal("new-sandbox:2.0", opts.ImageName);
        Assert.Equal("legacy-network", opts.NetworkMode);
    }
}
