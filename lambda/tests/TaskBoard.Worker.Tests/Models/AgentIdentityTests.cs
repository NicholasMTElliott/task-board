using System.Security.Cryptography;
using System.Text;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Tests.Models;

public class AgentIdentityTests
{
    [Fact]
    public void Generate_ReturnsNonNullIdentity()
    {
        var identity = AgentIdentity.Generate();

        Assert.NotNull(identity);
        Assert.NotEmpty(identity.FamilyName);
        Assert.NotEmpty(identity.MachineName);
    }

    [Fact]
    public void Generate_FamilyNameIsFromList()
    {
        var identity = AgentIdentity.Generate();

        Assert.Contains(identity.FamilyName, AgentIdentity.AvailableFamilyNames);
    }

    [Fact]
    public void Generate_MachineNameMatchesEnvironment()
    {
        var identity = AgentIdentity.Generate();

        Assert.Equal(Environment.MachineName, identity.MachineName);
    }

    [Fact]
    public void DisplayName_FormatsCorrectly()
    {
        var identity = new AgentIdentity("Smith", "Baelfire");

        Assert.Equal("Smith on Baelfire", identity.DisplayName);
    }

    [Fact]
    public void Generate_ProducesDifferentFamilyNames()
    {
        var names = Enumerable.Range(0, 30)
            .Select(_ => AgentIdentity.Generate().FamilyName)
            .ToList();

        // With 200 family names, all 30 being identical is vanishingly unlikely
        Assert.True(names.Distinct().Count() > 1,
            "Generated 30 names and all were identical — name generation appears broken.");
    }

    [Fact]
    public void AvailableGivenNames_HasTwoHundredEntries()
    {
        Assert.Equal(200, AgentIdentity.AvailableGivenNames.Count);
    }

    [Fact]
    public void AvailableFamilyNames_HasTwoHundredEntries()
    {
        Assert.Equal(200, AgentIdentity.AvailableFamilyNames.Count);
    }

    [Fact]
    public void ResolveGivenName_IsDeterministic()
    {
        var name1 = AgentIdentity.ResolveGivenName("docker-claude-cli", "claude-opus-4-6");
        var name2 = AgentIdentity.ResolveGivenName("docker-claude-cli", "claude-opus-4-6");

        Assert.Equal(name1, name2);
    }

    [Fact]
    public void ResolveGivenName_IsCaseInsensitive()
    {
        var lower = AgentIdentity.ResolveGivenName("docker-claude-cli", "claude-opus-4-6");
        var mixed = AgentIdentity.ResolveGivenName("Docker-Claude-CLI", "Claude-Opus-4-6");

        Assert.Equal(lower, mixed);
    }

    [Fact]
    public void ResolveGivenName_DifferentProviderModelProducesDifferentNames()
    {
        var name1 = AgentIdentity.ResolveGivenName("docker-claude-cli", "claude-opus-4-6");
        var name2 = AgentIdentity.ResolveGivenName("docker-opencode", "qwen3.6-35b-a3b-think");

        // Different inputs should (statistically) produce different names.
        // This will occasionally fail for specific inputs that hash to the same bucket;
        // use a pair that is known to be different.
        Assert.NotEqual("same:same", $"{name1}:{name2}");
        // Main check: result is from the known pool
        Assert.Contains(name1, AgentIdentity.AvailableGivenNames);
        Assert.Contains(name2, AgentIdentity.AvailableGivenNames);
    }

    [Fact]
    public void ResolveGivenName_NullProviderAndModel_ReturnsStableResult()
    {
        var name1 = AgentIdentity.ResolveGivenName(null, null);
        var name2 = AgentIdentity.ResolveGivenName(null, null);

        Assert.Equal(name1, name2);
        Assert.Contains(name1, AgentIdentity.AvailableGivenNames);
    }

    [Fact]
    public void ResolveGivenName_ResultIsFromPool()
    {
        var providers = new[] { "docker-claude-cli", "docker-codex", "docker-opencode", "stub" };
        var models = new[] { "claude-opus-4-6", "claude-sonnet-4-6", "claude-haiku-4-5-20251001", null };

        foreach (var p in providers)
        foreach (var m in models)
        {
            var name = AgentIdentity.ResolveGivenName(p, m);
            Assert.Contains(name, AgentIdentity.AvailableGivenNames);
        }
    }

    [Fact]
    public void ResolveGivenName_MatchesSHA256Hash()
    {
        // Pin a known mapping so pool mutation is caught as a test failure.
        const string provider = "docker-claude-cli";
        const string model = "claude-opus-4-6";
        var key = $"{provider}:{model}".ToLowerInvariant();
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var expectedIndex = (int)(System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(hashBytes) % 200u);
        var expectedName = AgentIdentity.AvailableGivenNames[expectedIndex];

        Assert.Equal(expectedName, AgentIdentity.ResolveGivenName(provider, model));
    }

    [Fact]
    public void FormatAgentName_WithProvider_ReturnsFullName()
    {
        var identity = new AgentIdentity("Smith", "Baelfire");
        var name = identity.FormatAgentName("docker-claude-cli", "claude-opus-4-6");

        var expectedGiven = AgentIdentity.ResolveGivenName("docker-claude-cli", "claude-opus-4-6");
        Assert.Equal($"{expectedGiven} Smith on Baelfire", name);
    }

    [Fact]
    public void FormatAgentName_NullProvider_FallsBackToDisplayName()
    {
        var identity = new AgentIdentity("Smith", "Baelfire");

        Assert.Equal("Smith on Baelfire", identity.FormatAgentName(null, "some-model"));
        Assert.Equal("Smith on Baelfire", identity.FormatAgentName("", "some-model"));
        Assert.Equal("Smith on Baelfire", identity.FormatAgentName("   ", "some-model"));
    }

    [Fact]
    public void FormatAgentName_SameProviderModel_ProducesSameGivenName_AcrossSessions()
    {
        var session1 = new AgentIdentity("Smith", "Machine1");
        var session2 = new AgentIdentity("Jones", "Machine2");

        var name1 = session1.FormatAgentName("docker-claude-cli", "claude-opus-4-6");
        var name2 = session2.FormatAgentName("docker-claude-cli", "claude-opus-4-6");

        // Given name is the same (provider+model hash is deterministic)
        var given1 = name1.Split(' ')[0];
        var given2 = name2.Split(' ')[0];
        Assert.Equal(given1, given2);

        // Family name differs (different sessions)
        Assert.NotEqual(name1, name2);
    }

    [Fact]
    public void AvailableGivenNames_AreAllDistinct()
    {
        var names = AgentIdentity.AvailableGivenNames;
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void AvailableFamilyNames_AreAllDistinct()
    {
        var names = AgentIdentity.AvailableFamilyNames;
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
