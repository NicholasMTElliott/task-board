using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using TaskBoard.Worker.Tests.Helpers;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Integration tests proving that workflow config hot-reload is deferred to
/// phase boundaries — in-flight runs are never disturbed.
/// </summary>
public class PollingRunnerConfigReloadTests
{
    private const string BoardId = "board-1";
    private const string Workspace = "C:/fake/workspace";

    // ── Minimal workflow configs ────────────────────────────────────────────────

    private static WorkflowConfig MakeConfig(string triggerColumn = "Ready", string terminalColumn = "Done") =>
        new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                [triggerColumn] = new WorkflowState(
                    Name: triggerColumn,
                    Role: "se",
                    GateType: "agent_run",
                    TaskPrompt: "Do it.",
                    Transitions: new Dictionary<string, TransitionTarget>
                    {
                        ["IN_PROGRESS"] = TransitionTarget.ForColumn("Doing"),
                        ["COMPLETE"]    = TransitionTarget.ForColumn(terminalColumn),
                        ["ERROR"]       = TransitionTarget.ForColumn("Error"),
                    },
                    PipelineOrder: 1),
                ["Doing"] = new WorkflowState("Doing", null, "in_progress", null,
                    new Dictionary<string, TransitionTarget>()),
                [terminalColumn] = new WorkflowState(terminalColumn, null, "terminal", null,
                    new Dictionary<string, TransitionTarget>()),
                ["Error"] = new WorkflowState("Error", null, "holding", null,
                    new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["se"] = new WorkflowRole("claude-opus-4-6", "You are an engineer.",
                    new List<string> { "Design" }),
            }).Normalised();

    // ── Tests ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// PollingRunner refreshes workflowConfig at the start of each cycle
    /// (not mid-cycle). Swapping the provider's current config between cycles
    /// means the NEXT cycle uses the new config.
    ///
    /// Observable signal: GetBoardCardsAsync receives the terminal-column list
    /// derived from the active config. Config A has terminal "Done"; config B
    /// has terminal "DoneV2". After swapping we verify the terminal column
    /// "DoneV2" is excluded in the next fetch.
    /// </summary>
    [Fact]
    public async Task PollingRunner_ConfigUpdatedBetweenCycles_NewConfigUsedOnNextCycle()
    {
        var configA = MakeConfig("Ready", "Done");
        var configB = MakeConfig("Ready", "DoneV2");

        var provider = TestWorkflowConfigProvider.Create(configA);

        var boardClient = Substitute.For<ITaskBoardClient>();

        // Track excluded columns per call.
        var excludedPerCall = new List<IReadOnlyList<string>?>();
        var callCount = 0;

        boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(ci =>
            {
                var excluded = ci.ArgAt<IReadOnlyList<string>?>(2);
                excludedPerCall.Add(excluded);
                callCount++;

                // After the first fetch completes, swap to configB so the next
                // cycle picks up the new terminal column.
                if (callCount == 1)
                    provider.SetCurrentForTesting(configB);

                return Task.FromResult<IReadOnlyList<BoardCard>>(new List<BoardCard>());
            });

        var agentRunner = MakeAgentRunner(boardClient, provider);
        var mergeRunner = MakeMergeRunner(boardClient, provider);
        var completionRunner = new CompletionRunner(boardClient, new StubCrossReferenceResolver(),
            provider, new AgentIdentity("Agent", "TestMachine"), NullLogger<CompletionRunner>.Instance);

        var pollingRunner = new PollingRunner(
            boardClient,
            agentRunner,
            mergeRunner,
            completionRunner,
            provider,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            NullLogger<PollingRunner>.Instance);

        // Run two cycles then stop.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await pollingRunner.RunAsync(BoardId, Workspace, TimeSpan.FromMilliseconds(10), cts.Token);

        // Verify: first cycle saw "Done" as excluded, second cycle saw "DoneV2".
        Assert.True(excludedPerCall.Count >= 2,
            $"Expected at least 2 board fetches, got {excludedPerCall.Count}");

        var firstExcluded = excludedPerCall[0] ?? [];
        var secondExcluded = excludedPerCall[1] ?? [];

        Assert.Contains("Done", firstExcluded);
        Assert.DoesNotContain("DoneV2", firstExcluded);

        Assert.Contains("DoneV2", secondExcluded);
    }

    /// <summary>
    /// AgentRunner snapshots workflowConfig at phase entry (the very start of
    /// ExecuteAsync). Changes to the provider after the snapshot are invisible
    /// to the in-progress run.
    ///
    /// Observable signal: the IN_PROGRESS transition (MoveCardToColumnAsync)
    /// uses config A's column "Doing". After that call we swap to config B
    /// which has a different ERROR column "ErrorV2". The outcome transition
    /// (which fires after the step fails due to missing git) must still use
    /// config A's ERROR column "Error", not "ErrorV2".
    /// </summary>
    [Fact]
    public async Task AgentRunner_ConfigChangedDuringRun_SnapshotIsStableForDuration()
    {
        var configA = MakeConfig("Ready", "Done"); // ERROR → "Error"
        // Build configB with a different error column so we can distinguish.
        var configB = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready"] = new WorkflowState(
                    Name: "Ready",
                    Role: "se",
                    GateType: "agent_run",
                    TaskPrompt: "Do it.",
                    Transitions: new Dictionary<string, TransitionTarget>
                    {
                        ["IN_PROGRESS"] = TransitionTarget.ForColumn("Doing"),
                        ["COMPLETE"]    = TransitionTarget.ForColumn("Done"),
                        ["ERROR"]       = TransitionTarget.ForColumn("ErrorV2"),  // differs from configA
                    },
                    PipelineOrder: 1),
                ["Doing"] = new WorkflowState("Doing", null, "in_progress", null,
                    new Dictionary<string, TransitionTarget>()),
                ["Done"] = new WorkflowState("Done", null, "terminal", null,
                    new Dictionary<string, TransitionTarget>()),
                ["ErrorV2"] = new WorkflowState("ErrorV2", null, "holding", null,
                    new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["se"] = new WorkflowRole("claude-opus-4-6", "You are an engineer.",
                    new List<string> { "Design" }),
            }).Normalised();

        var provider = TestWorkflowConfigProvider.Create(configA);

        var boardClient = Substitute.For<ITaskBoardClient>();

        boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(Task.FromResult<IReadOnlyList<BoardCard>>(new List<BoardCard>
            {
                new("42", "My card", "body", "Ready"),
            }));

        boardClient.GetCardCommentsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CardComment>>(new List<CardComment>()));

        // When the IN_PROGRESS move is called (first MoveCardToColumnAsync),
        // swap the provider so subsequent config reads would see configB.
        boardClient.MoveCardToColumnAsync("42", "Doing", Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                provider.SetCurrentForTesting(configB);
                return Task.CompletedTask;
            });

        var agentRunner = MakeAgentRunner(boardClient, provider);

        await agentRunner.ExecuteAsync("42", BoardId, Workspace, CancellationToken.None);

        // The run used configA's snapshot throughout:
        // - IN_PROGRESS column was "Doing" (config A and B agree here — this is just a confirmation)
        await boardClient.Received().MoveCardToColumnAsync("42", "Doing", Arg.Any<CancellationToken>());

        // Outcome column must be "Error" (config A), NOT "ErrorV2" (config B).
        // The run fails because there's no real git workspace; it routes to ERROR.
        await boardClient.Received().MoveCardToColumnAsync("42", "Error", Arg.Any<CancellationToken>());
        await boardClient.DidNotReceive().MoveCardToColumnAsync("42", "ErrorV2", Arg.Any<CancellationToken>());
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static AgentRunner MakeAgentRunner(ITaskBoardClient boardClient, WorkflowConfigProvider provider) =>
        new AgentRunner(
            boardClient,
            AgentExecutorResolver.ForSingleExecutor(new StubAgentExecutor(NullLogger<StubAgentExecutor>.Instance)),
            new TaskFileManager(NullLogger<TaskFileManager>.Instance),
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            provider,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Agent", "TestMachine"),
            new UpdateFileProcessor(boardClient, provider, new AgentIdentity("Agent", "TestMachine"),
                NullLogger<UpdateFileProcessor>.Instance),
            NullRunStore.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            TaskBoard.Worker.Tests.Helpers.TestTenant.Instance,
            NullLogger<AgentRunner>.Instance);

    private static MergeRunner MakeMergeRunner(ITaskBoardClient boardClient, WorkflowConfigProvider provider) =>
        new MergeRunner(
            boardClient,
            new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance),
            provider,
            new AgentIdentity("Agent", "TestMachine"),
            Substitute.For<ICrossReferenceResolver>(),
            Substitute.For<IAgentExecutorResolver>(),
            NullLogger<MergeRunner>.Instance);
}
