using Microsoft.Extensions.Configuration;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

public class DockerClaudeAgentOptionsTests
{
    // ── Default values ───────────────────────────────────────────────────────

    [Fact]
    public void DockerClaudeAgentOptions_Defaults_AreCorrect()
    {
        var opts = new DockerClaudeAgentOptions();

        Assert.Equal("aiboard-agent-sandbox:latest", opts.ImageName);
        Assert.Equal("host", opts.NetworkMode);
        Assert.Equal("", opts.ContainerUser);
        Assert.Null(opts.MemoryLimit);
        Assert.Null(opts.CpuLimit);
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

    // ── Config binding (new section) ─────────────────────────────────────────

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
            ["DockerAgents:Claude:MountHostDockerSocket"] = "true",
            ["DockerAgents:Claude:HostDockerSocketPath"] = "/custom/docker.sock",
            ["DockerAgents:Claude:ContainerDockerSocketPath"] = "/run/docker.sock",
            ["DockerAgents:Claude:CredentialPath"] = "/home/user/.claude",
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
        Assert.True(opts.MountHostDockerSocket);
        Assert.Equal("/custom/docker.sock", opts.HostDockerSocketPath);
        Assert.Equal("/run/docker.sock", opts.ContainerDockerSocketPath);
        Assert.Equal("/home/user/.claude", opts.CredentialPath);
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
        // Only override NetworkMode; other fields should retain defaults
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

    // ── Legacy section still works ───────────────────────────────────────────

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
        // Simulate the Program.cs dual-bind sequence: legacy first, new second.
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

        // New section wins on ImageName
        Assert.Equal("new-sandbox:2.0", opts.ImageName);
        // Legacy survives where new is unset
        Assert.Equal("legacy-network", opts.NetworkMode);
    }
}
