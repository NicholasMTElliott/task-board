using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Data;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests;

public class OrchestratorTests
{
    private readonly ITrelloClient _trelloClient = Substitute.For<ITrelloClient>();
    private readonly ILlmClient _llmClient = Substitute.For<ILlmClient>();
    private readonly ICardStateRepository _cardStateRepository = Substitute.For<ICardStateRepository>();
    private readonly IRunLogRepository _runLogRepository = Substitute.For<IRunLogRepository>();

    private static readonly WorkflowConfig DefaultConfig = new(
        States: new Dictionary<string, WorkflowState>
        {
            ["list-requirements"] = new WorkflowState(
                "Requirements", "business_analyst", "agent_run",
                "Analyze the card.", new Dictionary<string, string>
                {
                    ["COMPLETE"] = "list-requirements-review",
                    ["NEEDS_INFO"] = "list-questions",
                    ["ERROR"] = "list-error"
                }),
            ["list-requirements-review"] = new WorkflowState(
                "Requirements Review", null, "manual_gate", null,
                new Dictionary<string, string>()),
            ["list-questions"] = new WorkflowState(
                "Questions", null, "holding", null,
                new Dictionary<string, string>()),
            ["list-error"] = new WorkflowState(
                "Error", null, "holding", null,
                new Dictionary<string, string>())
        },
        Roles: new Dictionary<string, WorkflowRole>
        {
            ["business_analyst"] = new WorkflowRole(
                "gpt-4.1",
                "You are a Business Analyst.",
                new List<string> { "Requirements", "Open Questions", "Acceptance Criteria" })
        });

    private Orchestrator CreateSut(WorkflowConfig? config = null)
    {
        return new Orchestrator(
            config ?? DefaultConfig,
            _trelloClient,
            _llmClient,
            _cardStateRepository,
            _runLogRepository,
            Options.Create(new QueueProcessingOptions()),
            NullLogger<Orchestrator>.Instance);
    }

    private static QueueMessage MakeMessage(string listAfterId, string cardId = "card-1", string actionId = "action-1")
    {
        var payload = $$"""
            {
                "action": {
                    "id": "{{actionId}}",
                    "data": {
                        "card": { "id": "{{cardId}}" },
                        "listAfter": { "id": "{{listAfterId}}" },
                        "listBefore": { "id": "list-before" }
                    }
                }
            }
            """;
        return new QueueMessage(1, actionId, cardId, payload, DateTimeOffset.UtcNow, 1);
    }

    [Fact]
    public async Task AgentRunState_FullPipelineExecutes()
    {
        var message = MakeMessage("list-requirements");

        _cardStateRepository
            .TryAcquireLockAsync("card-1", Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _trelloClient
            .GetCardAsync("card-1", Arg.Any<CancellationToken>())
            .Returns(new TrelloCard("card-1", "Test Card", "# Requirements\nOld content", "list-before"));

        _llmClient
            .GetCompletionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResponse(
                new Dictionary<string, string> { ["Requirements"] = "New requirements" },
                "Agent complete.",
                "COMPLETE",
                false));

        var sut = CreateSut();
        await sut.ExecuteAsync(message, CancellationToken.None);

        await _trelloClient.Received(1).GetCardAsync("card-1", Arg.Any<CancellationToken>());
        await _llmClient.Received(1).GetCompletionAsync("gpt-4.1", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _trelloClient.Received(1).UpdateCardDescriptionAsync("card-1", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _trelloClient.Received(1).UpsertAgentCommentAsync("card-1", "Agent complete.", Arg.Any<CancellationToken>());
        await _trelloClient.Received(1).MoveCardToListAsync("card-1", "list-requirements-review", Arg.Any<CancellationToken>());
        await _runLogRepository.Received(1).WriteAsync("card-1", "business_analyst",
            Arg.Is<string?>(h => h != null && h.Length == 64),
            Arg.Is<string?>(h => h != null && h.Length == 64),
            "COMPLETE", Arg.Any<CancellationToken>());
        await _cardStateRepository.Received(1).ReleaseLockAsync("card-1", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ManualGateState_ReturnsEarly()
    {
        var message = MakeMessage("list-requirements-review");

        var sut = CreateSut();
        await sut.ExecuteAsync(message, CancellationToken.None);

        await _cardStateRepository.DidNotReceive().TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _trelloClient.DidNotReceive().GetCardAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _llmClient.DidNotReceive().GetCompletionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnknownListId_ReturnsEarly()
    {
        var message = MakeMessage("list-unknown");

        var sut = CreateSut();
        await sut.ExecuteAsync(message, CancellationToken.None);

        await _cardStateRepository.DidNotReceive().TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LockAlreadyHeld_ReturnsEarly()
    {
        var message = MakeMessage("list-requirements");

        _cardStateRepository
            .TryAcquireLockAsync("card-1", Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var sut = CreateSut();
        await sut.ExecuteAsync(message, CancellationToken.None);

        await _trelloClient.DidNotReceive().GetCardAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _llmClient.DidNotReceive().GetCompletionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NeedsInfoOutcome_MovesToQuestionsAndSetsOriginListId()
    {
        var message = MakeMessage("list-requirements");

        _cardStateRepository
            .TryAcquireLockAsync("card-1", Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _trelloClient
            .GetCardAsync("card-1", Arg.Any<CancellationToken>())
            .Returns(new TrelloCard("card-1", "Test Card", "# Requirements\ncontent", "list-requirements"));

        _llmClient
            .GetCompletionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResponse(
                new Dictionary<string, string>(),
                "Need more info.",
                "NEEDS_INFO",
                false));

        var sut = CreateSut();
        await sut.ExecuteAsync(message, CancellationToken.None);

        await _trelloClient.Received(1).MoveCardToListAsync("card-1", "list-questions", Arg.Any<CancellationToken>());
        await _cardStateRepository.Received(1).UpdateStateAsync(
            "card-1", "action-1", "list-questions",
            "list-requirements", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ErrorOutcome_MovesToErrorList()
    {
        var message = MakeMessage("list-requirements");

        _cardStateRepository
            .TryAcquireLockAsync("card-1", Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _trelloClient
            .GetCardAsync("card-1", Arg.Any<CancellationToken>())
            .Returns(new TrelloCard("card-1", "Test Card", "content", "list-requirements"));

        _llmClient
            .GetCompletionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResponse(
                new Dictionary<string, string>(),
                "Error occurred.",
                "ERROR",
                false));

        var sut = CreateSut();
        await sut.ExecuteAsync(message, CancellationToken.None);

        await _trelloClient.Received(1).MoveCardToListAsync("card-1", "list-error", Arg.Any<CancellationToken>());
        await _runLogRepository.Received(1).WriteAsync("card-1", "business_analyst",
            Arg.Any<string?>(), Arg.Any<string?>(), "ERROR", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidLlmOutcome_TreatedAsError()
    {
        var message = MakeMessage("list-requirements");

        _cardStateRepository
            .TryAcquireLockAsync("card-1", Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _trelloClient
            .GetCardAsync("card-1", Arg.Any<CancellationToken>())
            .Returns(new TrelloCard("card-1", "Test Card", "content", "list-requirements"));

        _llmClient
            .GetCompletionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResponse(
                new Dictionary<string, string>(),
                "Something weird.",
                "INVALID_OUTCOME",
                false));

        var sut = CreateSut();
        await sut.ExecuteAsync(message, CancellationToken.None);

        await _trelloClient.Received(1).MoveCardToListAsync("card-1", "list-error", Arg.Any<CancellationToken>());
        await _runLogRepository.Received(1).WriteAsync("card-1", "business_analyst",
            Arg.Any<string?>(), Arg.Any<string?>(), "ERROR", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrelloReadFailure_ExceptionPropagates_LockStillReleased()
    {
        var message = MakeMessage("list-requirements");

        _cardStateRepository
            .TryAcquireLockAsync("card-1", Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _trelloClient
            .GetCardAsync("card-1", Arg.Any<CancellationToken>())
            .Returns<TrelloCard>(_ => throw new HttpRequestException("Trello API down"));

        var sut = CreateSut();
        await Assert.ThrowsAsync<HttpRequestException>(() => sut.ExecuteAsync(message, CancellationToken.None));

        await _cardStateRepository.Received(1).ReleaseLockAsync("card-1", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NoListAfterId_ReturnsEarly()
    {
        var message = new QueueMessage(1, "action-1", "card-1",
            """{"action": {"data": {}}}""",
            DateTimeOffset.UtcNow, 1);

        var sut = CreateSut();
        await sut.ExecuteAsync(message, CancellationToken.None);

        await _cardStateRepository.DidNotReceive().TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReturnFromQuestions_CorrectOrigin_Processes()
    {
        var message = MakeMessage("list-requirements");

        _cardStateRepository
            .GetStateAsync("card-1", Arg.Any<CancellationToken>())
            .Returns(new CardState("card-1", "list-requirements", true));

        _cardStateRepository
            .TryAcquireLockAsync("card-1", Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _trelloClient
            .GetCardAsync("card-1", Arg.Any<CancellationToken>())
            .Returns(new TrelloCard("card-1", "Test Card", "content", "list-questions"));

        _llmClient
            .GetCompletionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResponse(
                new Dictionary<string, string>(),
                "Done.",
                "COMPLETE",
                false));

        var sut = CreateSut();
        await sut.ExecuteAsync(message, CancellationToken.None);

        await _trelloClient.Received(1).GetCardAsync("card-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReturnFromQuestions_WrongOrigin_Skips()
    {
        var message = MakeMessage("list-requirements");

        _cardStateRepository
            .GetStateAsync("card-1", Arg.Any<CancellationToken>())
            .Returns(new CardState("card-1", "list-design", true));

        var sut = CreateSut();
        await sut.ExecuteAsync(message, CancellationToken.None);

        await _cardStateRepository.DidNotReceive().TryAcquireLockAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _trelloClient.DidNotReceive().GetCardAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingTransition_FallsBackToError()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, string>
                    {
                        ["COMPLETE"] = "list-review",
                        ["ERROR"] = "list-error"
                    }),
                ["list-review"] = new WorkflowState("Review", null, "manual_gate", null, new Dictionary<string, string>()),
                ["list-error"] = new WorkflowState("Error", null, "holding", null, new Dictionary<string, string>())
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "You are a BA.", new List<string> { "Requirements" })
            });

        var message = MakeMessage("list-req");

        _cardStateRepository
            .TryAcquireLockAsync("card-1", Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _trelloClient
            .GetCardAsync("card-1", Arg.Any<CancellationToken>())
            .Returns(new TrelloCard("card-1", "Card", "content", "list-req"));

        _llmClient
            .GetCompletionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResponse(
                new Dictionary<string, string>(),
                "Blocked.",
                "BLOCKED",
                false));

        var sut = CreateSut(config);
        await sut.ExecuteAsync(message, CancellationToken.None);

        // BLOCKED has no transition, should fall back to ERROR
        await _trelloClient.Received(1).MoveCardToListAsync("card-1", "list-error", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MissingTransitionAndNoErrorFallback_CardStays()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-req"] = new WorkflowState(
                    "Requirements", "ba", "agent_run",
                    "Analyze.", new Dictionary<string, string>
                    {
                        ["COMPLETE"] = "list-review"
                    }),
                ["list-review"] = new WorkflowState("Review", null, "manual_gate", null, new Dictionary<string, string>())
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["ba"] = new WorkflowRole("gpt-4.1", "You are a BA.", new List<string> { "Requirements" })
            });

        var message = MakeMessage("list-req");

        _cardStateRepository
            .TryAcquireLockAsync("card-1", Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _trelloClient
            .GetCardAsync("card-1", Arg.Any<CancellationToken>())
            .Returns(new TrelloCard("card-1", "Card", "content", "list-req"));

        _llmClient
            .GetCompletionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new AgentResponse(
                new Dictionary<string, string>(),
                "Blocked.",
                "BLOCKED",
                false));

        var sut = CreateSut(config);
        await sut.ExecuteAsync(message, CancellationToken.None);

        // No transition for BLOCKED or ERROR — card should not be moved
        await _trelloClient.DidNotReceive().MoveCardToListAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
