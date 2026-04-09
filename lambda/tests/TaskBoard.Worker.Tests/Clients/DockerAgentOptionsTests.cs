using Microsoft.Extensions.Configuration;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests.Clients;

public class DockerAgentOptionsTests
{
    // ── Default values ───────────────────────────────────────────────────────

    [Fact]
    public void DockerAgentOptions_Defaults_AreCorrect()
    {
        var opts = new DockerAgentOptions();

        Assert.Equal("aiboard-agent-sandbox:latest", opts.ImageName);
        Assert.Equal("host", opts.NetworkMode);
        Assert.Equal("", opts.ContainerUser);
        Assert.Null(opts.MemoryLimit);
        Assert.Null(opts.CpuLimit);
        Assert.Equal("", opts.CredentialPath);
        Assert.Empty(opts.AdditionalMounts);
    }

    [Fact]
    public void DockerAgentOptions_SectionName_IsDocker()
    {
        Assert.Equal("Docker", DockerAgentOptions.SectionName);
    }

    // ── Config binding ───────────────────────────────────────────────────────

    [Fact]
    public void DockerAgentOptions_BindsAllScalarPropertiesFromConfiguration()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["Docker:ImageName"] = "custom-sandbox:2.0",
            ["Docker:NetworkMode"] = "none",
            ["Docker:MemoryLimit"] = "4g",
            ["Docker:CpuLimit"] = "2.0",
            ["Docker:ContainerUser"] = "agent",
            ["Docker:CredentialPath"] = "/home/user/.claude",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var opts = new DockerAgentOptions();
        config.GetSection(DockerAgentOptions.SectionName).Bind(opts);

        Assert.Equal("custom-sandbox:2.0", opts.ImageName);
        Assert.Equal("none", opts.NetworkMode);
        Assert.Equal("4g", opts.MemoryLimit);
        Assert.Equal("2.0", opts.CpuLimit);
        Assert.Equal("agent", opts.ContainerUser);
        Assert.Equal("/home/user/.claude", opts.CredentialPath);
    }

    [Fact]
    public void DockerAgentOptions_AdditionalMounts_BindsListFromConfiguration()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["Docker:AdditionalMounts:0"] = "/host/data:/container/data",
            ["Docker:AdditionalMounts:1"] = "/host/tools:/container/tools:ro",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var opts = new DockerAgentOptions();
        config.GetSection(DockerAgentOptions.SectionName).Bind(opts);

        Assert.Equal(2, opts.AdditionalMounts.Count);
        Assert.Contains("/host/data:/container/data", opts.AdditionalMounts);
        Assert.Contains("/host/tools:/container/tools:ro", opts.AdditionalMounts);
    }

    [Fact]
    public void DockerAgentOptions_EmptyAdditionalMounts_RemainsEmpty()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["Docker:ImageName"] = "myimage:latest",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var opts = new DockerAgentOptions();
        config.GetSection(DockerAgentOptions.SectionName).Bind(opts);

        Assert.Empty(opts.AdditionalMounts);
    }

    [Fact]
    public void DockerAgentOptions_NullOptionalProperties_RemainsNullWhenNotSet()
    {
        var configValues = new Dictionary<string, string?>
        {
            ["Docker:ImageName"] = "myimage:latest",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var opts = new DockerAgentOptions();
        config.GetSection(DockerAgentOptions.SectionName).Bind(opts);

        Assert.Null(opts.MemoryLimit);
        Assert.Null(opts.CpuLimit);
    }

    [Fact]
    public void DockerAgentOptions_PartialConfig_PreservesDefaults()
    {
        // Only override NetworkMode; other fields should retain defaults
        var configValues = new Dictionary<string, string?>
        {
            ["Docker:NetworkMode"] = "none",
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(configValues)
            .Build();

        var opts = new DockerAgentOptions();
        config.GetSection(DockerAgentOptions.SectionName).Bind(opts);

        Assert.Equal("aiboard-agent-sandbox:latest", opts.ImageName);
        Assert.Equal("none", opts.NetworkMode);
    }
}
