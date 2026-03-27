using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Tests.Models;

public class AgentIdentityTests
{
    [Fact]
    public void Generate_ReturnsNonNullIdentity()
    {
        var identity = AgentIdentity.Generate();

        Assert.NotNull(identity);
        Assert.NotEmpty(identity.FirstName);
        Assert.NotEmpty(identity.LastName);
        Assert.NotEmpty(identity.MachineName);
    }

    [Fact]
    public void Generate_FirstNameIsFromList()
    {
        var identity = AgentIdentity.Generate();

        Assert.Contains(identity.FirstName, AgentIdentity.AvailableFirstNames);
    }

    [Fact]
    public void Generate_LastNameIsFromList()
    {
        var identity = AgentIdentity.Generate();

        Assert.Contains(identity.LastName, AgentIdentity.AvailableLastNames);
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
        var identity = new AgentIdentity("Mike", "Smith", "Baelfire");

        Assert.Equal("Mike Smith on Baelfire", identity.DisplayName);
    }

    [Fact]
    public void Generate_ProducesDifferentNames()
    {
        var names = Enumerable.Range(0, 20)
            .Select(_ => AgentIdentity.Generate().DisplayName)
            .ToList();

        // With 2,500 combinations, all 20 being identical is vanishingly unlikely
        Assert.True(names.Distinct().Count() > 1,
            "Generated 20 names and all were identical — name generation appears broken.");
    }

    [Fact]
    public void AvailableFirstNames_HasFiftyEntries()
    {
        Assert.Equal(50, AgentIdentity.AvailableFirstNames.Count);
    }

    [Fact]
    public void AvailableLastNames_HasFiftyEntries()
    {
        Assert.Equal(50, AgentIdentity.AvailableLastNames.Count);
    }
}
