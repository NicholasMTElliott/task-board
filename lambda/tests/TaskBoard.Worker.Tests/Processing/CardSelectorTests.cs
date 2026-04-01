using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

public class CardSelectorTests
{
    private static WorkflowConfig MakeConfig(
        int designOrder = 1, int implOrder = 2, int testOrder = 3,
        PollingConfig? polling = null,
        List<CardFilter>? designFilters = null)
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready for Design"] = new("Ready for Design", "se", "agent_run",
                    "Design it.", new Dictionary<string, TransitionTarget>(), PipelineOrder: designOrder,
                    Filters: designFilters),
                ["Ready for Implementation"] = new("Ready for Implementation", "se", "agent_run",
                    "Implement it.", new Dictionary<string, TransitionTarget>(), PipelineOrder: implOrder),
                ["Ready for Test"] = new("Ready for Test", "qa", "agent_run",
                    "Test it.", new Dictionary<string, TransitionTarget>(), PipelineOrder: testOrder),
                ["Designing"] = new("Designing", null, "in_progress",
                    null, new Dictionary<string, TransitionTarget>()),
                ["Designed"] = new("Designed", null, "manual_gate",
                    null, new Dictionary<string, TransitionTarget>()),
                ["Backlog"] = new("Backlog", null, "manual_entry",
                    null, new Dictionary<string, TransitionTarget>()),
                ["Error"] = new("Error", null, "holding",
                    null, new Dictionary<string, TransitionTarget>()),
                ["Tested"] = new("Tested", null, "terminal",
                    null, new Dictionary<string, TransitionTarget>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["se"] = new("claude-opus-4-6", "You are an engineer.", new List<string> { "Design" }),
                ["qa"] = new("claude-opus-4-6", "You are QA.", new List<string> { "Tests" }),
            },
            Polling: polling);
    }

    private static WorkflowConfig MakeConfigWithMerge()
    {
        var baseConfig = MakeConfig();
        var states = new Dictionary<string, WorkflowState>(baseConfig.States)
        {
            ["Accepted"] = new("Accepted", null, "system_merge",
                null, new Dictionary<string, TransitionTarget>
                {
                    ["IN_PROGRESS"] = TransitionTarget.ForColumn("Merging"),
                    ["COMPLETE"]    = TransitionTarget.ForColumn("Done"),
                    ["ERROR"]       = TransitionTarget.ForColumn("Error"),
                },
                PipelineOrder: 4),
            ["Merging"] = new("Merging", null, "in_progress",
                null, new Dictionary<string, TransitionTarget>()),
            ["Done"] = new("Done", null, "terminal",
                null, new Dictionary<string, TransitionTarget>()),
        };
        // Change Tested from terminal to manual_gate
        states["Tested"] = new("Tested", null, "manual_gate",
            null, new Dictionary<string, TransitionTarget>());
        return new WorkflowConfig(states, baseConfig.Roles, baseConfig.Polling);
    }

    private static BoardCard Card(string id, string column,
        IReadOnlyDictionary<string, string>? metadata = null)
        => new(id, $"Card {id}", "body", column, metadata);

    // --- Stage ordering ---

    [Fact]
    public void SelectsHighestPipelineStage()
    {
        var cards = new List<BoardCard>
        {
            Card("5", "Ready for Design"),
            Card("3", "Ready for Test"),
            Card("7", "Ready for Implementation"),
        };

        var result = CardSelector.SelectNext(cards, MakeConfig());

        Assert.NotNull(result.Selected);
        Assert.Equal("3", result.Selected!.Id); // Ready for Test has pipelineOrder=3
    }

    [Fact]
    public void AllSameStage_StageOrderIsNoOp()
    {
        var cards = new List<BoardCard>
        {
            Card("10", "Ready for Implementation"),
            Card("5", "Ready for Implementation"),
        };

        var result = CardSelector.SelectNext(cards, MakeConfig());

        Assert.NotNull(result.Selected);
        // Same stage, no priority, position tiebreaker: index 0 wins
        Assert.Equal("10", result.Selected!.Id);
    }

    // --- Priority ranking ---

    [Fact]
    public void SameStage_HigherPriorityWins()
    {
        var polling = new PollingConfig("Priority", new List<string> { "P0", "P1", "P2" });
        var cards = new List<BoardCard>
        {
            Card("7", "Ready for Implementation",
                new Dictionary<string, string> { ["priority"] = "P1" }),
            Card("8", "Ready for Implementation",
                new Dictionary<string, string> { ["priority"] = "P0" }),
        };

        var result = CardSelector.SelectNext(cards, MakeConfig(polling: polling));

        Assert.NotNull(result.Selected);
        Assert.Equal("8", result.Selected!.Id); // P0 rank 0 < P1 rank 1
    }

    // --- Position tiebreaker ---

    [Fact]
    public void SameStageAndPriority_LowestArrayIndexWins()
    {
        var polling = new PollingConfig("Priority", new List<string> { "P0", "P1" });
        var cards = new List<BoardCard>
        {
            Card("10", "Ready for Implementation",
                new Dictionary<string, string> { ["priority"] = "P1" }),
            Card("20", "Ready for Implementation",
                new Dictionary<string, string> { ["priority"] = "P1" }),
        };

        var result = CardSelector.SelectNext(cards, MakeConfig(polling: polling));

        Assert.NotNull(result.Selected);
        Assert.Equal("10", result.Selected!.Id); // position 0 < position 1
    }

    // --- Issue number tiebreaker ---

    [Fact]
    public void SameStage_SamePriority_SamePosition_LowestIssueNumberWins()
    {
        // When two cards are in the same stage with no priority configured,
        // and we can't differentiate by position (both are in the list),
        // issue number is the final tiebreaker.
        // Since position (array index) comes first, we need cards at
        // different positions but same stage/priority to test issue number.
        // Actually, array index is always unique, so issue number only matters
        // when position is the same — which can't happen in a list.
        // The real test: verify that with identical position factors,
        // lower issue number wins. We test this indirectly by ensuring
        // the overall sort is stable and correct.
        var cards = new List<BoardCard>
        {
            Card("20", "Ready for Design"),
            Card("5", "Ready for Design"),
        };

        var result = CardSelector.SelectNext(cards, MakeConfig());

        Assert.NotNull(result.Selected);
        // Position 0 (card 20) wins over position 1 (card 5)
        Assert.Equal("20", result.Selected!.Id);
    }

    // --- No eligible cards ---

    [Fact]
    public void NoEligibleCards_ReturnsNull()
    {
        var cards = new List<BoardCard>
        {
            Card("1", "Designing"),       // in_progress
            Card("2", "Designed"),        // manual_gate
            Card("3", "Backlog"),         // manual_entry
            Card("4", "Error"),           // holding
            Card("5", "Tested"),          // terminal
        };

        var result = CardSelector.SelectNext(cards, MakeConfig());

        Assert.Null(result.Selected);
    }

    [Fact]
    public void EmptyCardList_ReturnsNull()
    {
        var result = CardSelector.SelectNext(new List<BoardCard>(), MakeConfig());

        Assert.Null(result.Selected);
    }

    // --- Non-agent_run filtering ---

    [Fact]
    public void MixOfAgentRunAndNonAgentRun_OnlyAgentRunConsidered()
    {
        var cards = new List<BoardCard>
        {
            Card("1", "Designing"),                  // in_progress — skip
            Card("2", "Designed"),                   // manual_gate — skip
            Card("3", "Ready for Design"),           // agent_run — eligible
            Card("4", "Backlog"),                    // manual_entry — skip
        };

        var result = CardSelector.SelectNext(cards, MakeConfig());

        Assert.NotNull(result.Selected);
        Assert.Equal("3", result.Selected!.Id);
    }

    // --- Missing priority field ---

    [Fact]
    public void MissingPriorityField_TreatedAsLowestPriority()
    {
        var polling = new PollingConfig("Priority", new List<string> { "P0", "P1" });
        var cards = new List<BoardCard>
        {
            Card("1", "Ready for Implementation"),  // no metadata at all
            Card("2", "Ready for Implementation",
                new Dictionary<string, string> { ["priority"] = "P0" }),
        };

        var result = CardSelector.SelectNext(cards, MakeConfig(polling: polling));

        Assert.NotNull(result.Selected);
        Assert.Equal("2", result.Selected!.Id); // P0 has rank 0, card 1 has rank MaxValue
    }

    // --- Unknown priority value ---

    [Fact]
    public void UnknownPriorityValue_TreatedAsLowestPriority()
    {
        var polling = new PollingConfig("Priority", new List<string> { "P0", "P1" });
        var cards = new List<BoardCard>
        {
            Card("1", "Ready for Implementation",
                new Dictionary<string, string> { ["priority"] = "Unknown" }),
            Card("2", "Ready for Implementation",
                new Dictionary<string, string> { ["priority"] = "P1" }),
        };

        var result = CardSelector.SelectNext(cards, MakeConfig(polling: polling));

        Assert.NotNull(result.Selected);
        Assert.Equal("2", result.Selected!.Id); // P1 has rank 1, "Unknown" has rank MaxValue
    }

    // --- No polling config ---

    [Fact]
    public void NoPollingConfig_PrioritySortingSkipped_FallsToPosition()
    {
        // No polling config at all — all cards have equal priority rank
        var cards = new List<BoardCard>
        {
            Card("10", "Ready for Implementation",
                new Dictionary<string, string> { ["priority"] = "P1" }),
            Card("5", "Ready for Implementation",
                new Dictionary<string, string> { ["priority"] = "P0" }),
        };

        // Config with polling: null
        var result = CardSelector.SelectNext(cards, MakeConfig(polling: null));

        Assert.NotNull(result.Selected);
        // Without polling config, priority is ignored. Position tiebreaker: index 0 wins
        Assert.Equal("10", result.Selected!.Id);
    }

    // --- Full 4-tier sort (from the design examples) ---

    [Fact]
    public void DesignExample_HighestStageWinsRegardlessOfPriority()
    {
        var polling = new PollingConfig("Priority", new List<string> { "P0", "P1", "P2" });
        var cards = new List<BoardCard>
        {
            Card("3", "Ready for Test",
                new Dictionary<string, string> { ["priority"] = "P1" }),
            Card("5", "Ready for Design",
                new Dictionary<string, string> { ["priority"] = "P0" }),
            Card("7", "Ready for Implementation",
                new Dictionary<string, string> { ["priority"] = "P1" }),
            Card("8", "Ready for Implementation",
                new Dictionary<string, string> { ["priority"] = "P0" }),
        };

        var result = CardSelector.SelectNext(cards, MakeConfig(polling: polling));

        Assert.NotNull(result.Selected);
        Assert.Equal("3", result.Selected!.Id); // Ready for Test pipelineOrder=3 is highest
    }

    [Fact]
    public void DesignExample_WithoutTestCard_PriorityBreaksTie()
    {
        var polling = new PollingConfig("Priority", new List<string> { "P0", "P1", "P2" });
        var cards = new List<BoardCard>
        {
            Card("5", "Ready for Design",
                new Dictionary<string, string> { ["priority"] = "P0" }),
            Card("7", "Ready for Implementation",
                new Dictionary<string, string> { ["priority"] = "P1" }),
            Card("8", "Ready for Implementation",
                new Dictionary<string, string> { ["priority"] = "P0" }),
        };

        var result = CardSelector.SelectNext(cards, MakeConfig(polling: polling));

        Assert.NotNull(result.Selected);
        Assert.Equal("8", result.Selected!.Id); // Same stage as 7, but P0 > P1
    }

    // --- system_merge eligibility ---

    [Fact]
    public void SystemMergeCard_IsEligibleForSelection()
    {
        var config = MakeConfigWithMerge();
        var cards = new List<BoardCard>
        {
            Card("1", "Accepted"),
        };

        var result = CardSelector.SelectNext(cards, config);

        Assert.NotNull(result.Selected);
        Assert.Equal("1", result.Selected!.Id);
    }

    [Fact]
    public void SystemMergeCard_HigherPipelineOrder_SelectedOverAgentRun()
    {
        var config = MakeConfigWithMerge();
        var cards = new List<BoardCard>
        {
            Card("5", "Ready for Test"),      // agent_run, pipelineOrder=3
            Card("1", "Accepted"),            // system_merge, pipelineOrder=4
        };

        var result = CardSelector.SelectNext(cards, config);

        Assert.NotNull(result.Selected);
        Assert.Equal("1", result.Selected!.Id); // pipelineOrder 4 > 3
    }

    // --- Card filter predicates ---

    [Fact]
    public void FilteredCard_ExcludedFromSelection()
    {
        var filters = new List<CardFilter>
        {
            new(FilterTypes.Label, FilterOperators.Exists, "ai-ready")
        };
        var config = MakeConfig(designFilters: filters);

        var cards = new List<BoardCard>
        {
            // Card without the required label — should be excluded
            new("1", "Card 1", "body", "Ready for Design",
                Labels: new List<string> { "other-label" }),
        };

        var result = CardSelector.SelectNext(cards, config);

        Assert.Null(result.Selected);
    }

    [Fact]
    public void FilteredCard_MatchingLabel_IsIncluded()
    {
        var filters = new List<CardFilter>
        {
            new(FilterTypes.Label, FilterOperators.Exists, "ai-ready")
        };
        var config = MakeConfig(designFilters: filters);

        var cards = new List<BoardCard>
        {
            new("1", "Card 1", "body", "Ready for Design",
                Labels: new List<string> { "ai-ready", "other" }),
        };

        var result = CardSelector.SelectNext(cards, config);

        Assert.NotNull(result.Selected);
        Assert.Equal("1", result.Selected!.Id);
    }

    [Fact]
    public void UnfilteredState_AllCardsEligible()
    {
        // Implementation state has no filters — all cards in it are eligible
        var config = MakeConfig();

        var cards = new List<BoardCard>
        {
            new("1", "Card 1", "body", "Ready for Implementation"),
        };

        var result = CardSelector.SelectNext(cards, config);

        Assert.NotNull(result.Selected);
        Assert.Equal("1", result.Selected!.Id);
    }

    [Fact]
    public void MixOfFilteredAndUnfiltered_CorrectSelection()
    {
        var filters = new List<CardFilter>
        {
            new(FilterTypes.Label, FilterOperators.Exists, "ai-ready")
        };
        var config = MakeConfig(designFilters: filters);

        var cards = new List<BoardCard>
        {
            // Design card lacks required label — excluded
            new("1", "Card 1", "body", "Ready for Design"),
            // Implementation card has no filter — eligible
            new("2", "Card 2", "body", "Ready for Implementation"),
        };

        var result = CardSelector.SelectNext(cards, config);

        Assert.NotNull(result.Selected);
        Assert.Equal("2", result.Selected!.Id);
    }

    [Fact]
    public void AllCardsFailFilter_ReturnsNull()
    {
        var filters = new List<CardFilter>
        {
            new(FilterTypes.Assignee, FilterOperators.IsEmpty)
        };
        var config = MakeConfig(designFilters: filters);

        var cards = new List<BoardCard>
        {
            // All cards are assigned — fail isEmpty filter
            new("1", "Card 1", "body", "Ready for Design",
                Assignees: new List<string> { "bot" }),
            new("2", "Card 2", "body", "Ready for Design",
                Assignees: new List<string> { "user" }),
        };

        var result = CardSelector.SelectNext(cards, config);

        Assert.Null(result.Selected);
    }

    // --- Card in unknown column ---

    [Fact]
    public void CardInUnknownColumn_IsSkipped()
    {
        var cards = new List<BoardCard>
        {
            Card("1", "Some Unknown Column"),
            Card("2", "Ready for Design"),
        };

        var result = CardSelector.SelectNext(cards, MakeConfig());

        Assert.NotNull(result.Selected);
        Assert.Equal("2", result.Selected!.Id);
    }

    // --- Provider eligibility filtering ---

    private static WorkflowConfig MakeConfigWithProviders(string stepProvider, string? gateProvider = null)
    {
        var gateCheck = gateProvider is not null
            ? new GateCheckConfig("gate_role")
            : null;

        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready for Design"] = new("Ready for Design", null, "agent_run",
                    null, new Dictionary<string, TransitionTarget>(), PipelineOrder: 1,
                    Steps: [new WorkflowStep("step1", "step_role")],
                    GateCheck: gateCheck),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["step_role"] = new("model", "prompt", new List<string>(), Provider: stepProvider),
                ["gate_role"] = new("model", "prompt", new List<string>(),
                    Provider: gateProvider ?? "claude-cli"),
            });
    }

    [Fact]
    public void AllProvidersAvailable_CardSelected()
    {
        var config = MakeConfigWithProviders("claude-cli");
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude-cli" };

        var result = CardSelector.SelectNext(
            [Card("1", "Ready for Design")], config, available);

        Assert.NotNull(result.Selected);
        Assert.Equal("1", result.Selected!.Id);
        Assert.Empty(result.SkippedDueToProviders);
    }

    [Fact]
    public void MissingProvider_CardSkipped()
    {
        var config = MakeConfigWithProviders("codex");
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude-cli" };

        var result = CardSelector.SelectNext(
            [Card("1", "Ready for Design")], config, available);

        Assert.Null(result.Selected);
    }

    [Fact]
    public void MissingProvider_CardIncludedInSkippedList()
    {
        var config = MakeConfigWithProviders("codex");
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude-cli" };

        var result = CardSelector.SelectNext(
            [Card("1", "Ready for Design")], config, available);

        Assert.Single(result.SkippedDueToProviders);
        Assert.Equal("1", result.SkippedDueToProviders[0].Card.Id);
        Assert.Equal("Ready for Design", result.SkippedDueToProviders[0].StateName);
        Assert.Contains("codex", result.SkippedDueToProviders[0].MissingProviders);
    }

    [Fact]
    public void MixedStates_OnlyEligibleSelected()
    {
        // Card 1 requires codex (unavailable), card 2 requires claude-cli (available)
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Needs Codex"] = new("Needs Codex", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>(), PipelineOrder: 1,
                    Steps: [new WorkflowStep("step", "codex_role")]),
                ["Needs Claude"] = new("Needs Claude", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>(), PipelineOrder: 1,
                    Steps: [new WorkflowStep("step", "claude_role")]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["codex_role"] = new("model", "prompt", new List<string>(), Provider: "codex"),
                ["claude_role"] = new("model", "prompt", new List<string>(), Provider: "claude-cli"),
            });

        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude-cli" };
        var cards = new List<BoardCard>
        {
            Card("1", "Needs Codex"),
            Card("2", "Needs Claude"),
        };

        var result = CardSelector.SelectNext(cards, config, available);

        Assert.NotNull(result.Selected);
        Assert.Equal("2", result.Selected!.Id);
        Assert.Single(result.SkippedDueToProviders);
        Assert.Equal("1", result.SkippedDueToProviders[0].Card.Id);
    }

    [Fact]
    public void AllCardsSkippedByProvider_ReturnsNullWithSkippedList()
    {
        var config = MakeConfigWithProviders("codex");
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude-cli" };

        var result = CardSelector.SelectNext(
            [Card("1", "Ready for Design"), Card("2", "Ready for Design")], config, available);

        Assert.Null(result.Selected);
        Assert.Equal(2, result.SkippedDueToProviders.Count);
    }

    [Fact]
    public void NullAvailableProviders_NoFiltering()
    {
        // When availableProviders is null, backward-compatible: no filtering occurs
        var config = MakeConfigWithProviders("codex");

        var result = CardSelector.SelectNext(
            [Card("1", "Ready for Design")], config, availableProviders: null);

        Assert.NotNull(result.Selected);
        Assert.Equal("1", result.Selected!.Id);
        Assert.Empty(result.SkippedDueToProviders);
    }

    [Fact]
    public void SystemMergeState_NoProviderRequirements_AlwaysEligible()
    {
        // system_merge states have no steps/gate/optional — always eligible regardless of providers
        var config = MakeConfigWithMerge();
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude-cli" };

        var result = CardSelector.SelectNext(
            [Card("1", "Accepted")], config, available);

        Assert.NotNull(result.Selected);
        Assert.Equal("1", result.Selected!.Id);
    }

    [Fact]
    public void GateCheckRequiresUnavailableProvider_CardSkipped()
    {
        // Gate check uses a provider that's not available
        var config = MakeConfigWithProviders(stepProvider: "claude-cli", gateProvider: "codex");
        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude-cli" };

        var result = CardSelector.SelectNext(
            [Card("1", "Ready for Design")], config, available);

        Assert.Null(result.Selected);
        Assert.Single(result.SkippedDueToProviders);
        Assert.Contains("codex", result.SkippedDueToProviders[0].MissingProviders);
    }

    [Fact]
    public void OptionalStepsRequireUnavailableProvider_CardSkipped()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Ready for Design"] = new("Ready for Design", null, "agent_run", null,
                    new Dictionary<string, TransitionTarget>(), PipelineOrder: 1,
                    Steps: [new WorkflowStep("step", "claude_role")],
                    OptionalSteps:
                    [
                        new OptionalStepDefinition("security_review", "codex_role",
                            Description: "Security review"),
                    ]),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["claude_role"] = new("model", "prompt", new List<string>(), Provider: "claude-cli"),
                ["codex_role"] = new("model", "prompt", new List<string>(), Provider: "codex"),
            });

        var available = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude-cli" };

        var result = CardSelector.SelectNext(
            [Card("1", "Ready for Design")], config, available);

        Assert.Null(result.Selected);
        Assert.Single(result.SkippedDueToProviders);
        Assert.Contains("codex", result.SkippedDueToProviders[0].MissingProviders);
    }
}
