using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

public class NullRunStoreTests
{
    private readonly IRunStore _store = NullRunStore.Instance;

    [Fact]
    public async Task CreateRunAsync_CompletesWithoutError()
    {
        var run = new RunRecord("run-1", "card-1", "Ready for Design", "TestAgent", null, 3);
        await _store.CreateRunAsync(run, CancellationToken.None);
        // No exception = pass
    }

    [Fact]
    public async Task UpdateRunProgressAsync_CompletesWithoutError()
    {
        await _store.UpdateRunProgressAsync("run-1", 1, CancellationToken.None);
        // No exception = pass
    }

    [Fact]
    public async Task CompleteRunAsync_CompletesWithoutError()
    {
        await _store.CompleteRunAsync("run-1", AgentOutcome.COMPLETE, null, CancellationToken.None);
        // No exception = pass
    }

    [Fact]
    public async Task SaveStepResultAsync_CompletesWithoutError()
    {
        var record = new StepResultRecord(
            "run-1", "card-1", "Ready for Design", "create_design", 0,
            "senior_engineer", "claude-opus-4-6", AgentOutcome.COMPLETE,
            null, null, null, null, null, null,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        await _store.SaveStepResultAsync(record, CancellationToken.None);
        // No exception = pass
    }

    [Fact]
    public async Task GetStepResultsForCardAsync_ReturnsEmptyList()
    {
        var results = await _store.GetStepResultsForCardAsync("card-1", null, CancellationToken.None);
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetStepResultsForCardAsync_WithStateName_ReturnsEmptyList()
    {
        var results = await _store.GetStepResultsForCardAsync("card-1", "Ready for Design", CancellationToken.None);
        Assert.Empty(results);
    }

    [Fact]
    public async Task GetLatestRunStepResultsAsync_ReturnsEmptyList()
    {
        var results = await _store.GetLatestRunStepResultsAsync("card-1", "Ready for Design", CancellationToken.None);
        Assert.Empty(results);
    }

    [Fact]
    public void Instance_IsSingleton()
    {
        Assert.Same(NullRunStore.Instance, NullRunStore.Instance);
    }
}
