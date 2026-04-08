using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using static TaskBoard.Worker.Tests.Helpers.TestGitHelper;

namespace TaskBoard.Worker.Tests;

/// <summary>
/// Unit tests for the ticket estimation feature:
/// - AgentOutputParser: parsing the estimate field from structured output
/// - AgentSchemas: estimate field presence in both schemas
/// - AgentRunner: prompt placeholder resolution with extra context
/// - AgentRunner: calibration ticket injection and estimate capture (integration)
/// - WorkflowConfig: EstimationConfig deserialization
/// </summary>
public class EstimationTests
{
    // ── AgentOutputParser: estimate parsing ───────────────────────────────────

    [Fact]
    public void ParseResult_StructuredOutput_WithEstimate_ParsesEstimate()
    {
        var json = """{"structured_output":{"outcome":"COMPLETE","detail":"About 2x the calibration.","estimate":4}}""";

        var result = AgentOutputParser.ParseResult(json);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
        Assert.Equal(4.0, result.Estimate);
    }

    [Fact]
    public void ParseResult_StructuredOutput_WithoutEstimate_EstimateIsNull()
    {
        var json = """{"structured_output":{"outcome":"COMPLETE","detail":"Done"}}""";

        var result = AgentOutputParser.ParseResult(json);

        Assert.Null(result.Estimate);
    }

    [Fact]
    public void ParseResult_StructuredOutput_EstimateIsNull_EstimateIsNull()
    {
        // OpenAI schema requires estimate to be present but can be null
        var json = """{"structured_output":{"outcome":"COMPLETE","estimate":null}}""";

        var result = AgentOutputParser.ParseResult(json);

        Assert.Null(result.Estimate);
    }

    [Fact]
    public void ParseResult_StructuredOutput_FractionalEstimate_ParsesCorrectly()
    {
        var json = """{"structured_output":{"outcome":"COMPLETE","estimate":2.5}}""";

        var result = AgentOutputParser.ParseResult(json);

        Assert.Equal(2.5, result.Estimate);
    }

    // ── AgentSchemas: estimate field ──────────────────────────────────────────

    [Fact]
    public void OutcomeSchema_HasEstimateProperty()
    {
        using var doc = JsonDocument.Parse(AgentSchemas.OutcomeSchema);
        Assert.True(
            doc.RootElement.GetProperty("properties").TryGetProperty("estimate", out _),
            "OutcomeSchema must define an 'estimate' property");
    }

    [Fact]
    public void OutcomeSchemaOpenAI_HasEstimateProperty()
    {
        using var doc = JsonDocument.Parse(AgentSchemas.OutcomeSchemaOpenAI);
        Assert.True(
            doc.RootElement.GetProperty("properties").TryGetProperty("estimate", out _),
            "OutcomeSchemaOpenAI must define an 'estimate' property");
    }

    [Fact]
    public void OutcomeSchemaOpenAI_EstimateInRequired()
    {
        using var doc = JsonDocument.Parse(AgentSchemas.OutcomeSchemaOpenAI);
        var required = doc.RootElement.GetProperty("required")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Contains("estimate", required);
    }

    [Fact]
    public void OutcomeSchemaOpenAI_EstimateAllowsNull()
    {
        using var doc = JsonDocument.Parse(AgentSchemas.OutcomeSchemaOpenAI);
        var estimateProp = doc.RootElement.GetProperty("properties").GetProperty("estimate");
        // type should be ["number", "null"]
        var types = estimateProp.GetProperty("type")
            .EnumerateArray()
            .Select(e => e.GetString())
            .ToList();
        Assert.Contains("null", types);
    }

    // ── AgentRunner: ResolvePromptPlaceholders with extraContext ──────────────

    [Fact]
    public void ResolvePromptPlaceholders_ExtraContext_ReplacesPlaceholders()
    {
        var card = new BoardCard("42", "My Task", "", "col");
        var template = "Ticket {TaskId}: {TaskName}. Calibration: #{CalibrationTicketId} at {CalibrationSize} pts. Scale: {EstimationScale}.";
        var extra = new Dictionary<string, string>
        {
            ["CalibrationTicketId"] = "34",
            ["CalibrationSize"] = "1",
            ["EstimationScale"] = "1, 2, 4, 8",
        };

        var result = AgentRunner.ResolvePromptPlaceholders(template, card, extra);

        Assert.Equal("Ticket 42: My Task. Calibration: #34 at 1 pts. Scale: 1, 2, 4, 8.", result);
    }

    [Fact]
    public void ResolvePromptPlaceholders_NoExtraContext_UnknownPlaceholdersUnchanged()
    {
        var card = new BoardCard("1", "Task", "", "col");
        var template = "Hello {TaskName} — {CalibrationTicketId}";

        var result = AgentRunner.ResolvePromptPlaceholders(template, card);

        Assert.Equal("Hello Task — {CalibrationTicketId}", result);
    }

    [Fact]
    public void ResolvePromptPlaceholders_ExtraContextDoesNotOverrideBuiltIns()
    {
        var card = new BoardCard("99", "Real Name", "", "col");
        var extra = new Dictionary<string, string> { ["TaskId"] = "OVERRIDE" };
        var template = "ID={TaskId}";

        // Built-in "TaskId" should resolve to card.Id, not the extra context value,
        // because the switch handles TaskId before falling through to extraContext
        var result = AgentRunner.ResolvePromptPlaceholders(template, card, extra);

        Assert.Equal("ID=99", result);
    }

    [Fact]
    public void ResolvePromptPlaceholders_NullExtraContext_UnknownPlaceholdersUnchanged()
    {
        var card = new BoardCard("5", "Task", "", "col");
        var template = "{TaskId}: {Unknown}";

        var result = AgentRunner.ResolvePromptPlaceholders(template, card, null);

        Assert.Equal("5: {Unknown}", result);
    }

    // ── WorkflowConfig: EstimationConfig ──────────────────────────────────────

    [Fact]
    public void EstimationConfig_DefaultFieldName_IsEstimate()
    {
        var config = new EstimationConfig("34", 1);
        Assert.Equal("Estimate", config.FieldName);
    }

    [Fact]
    public void EstimationConfig_DefaultScale_IsNull()
    {
        var config = new EstimationConfig("34", 1);
        Assert.Null(config.Scale);
    }

    [Fact]
    public void WorkflowConfig_WithEstimation_RoundTripsViaJson()
    {
        var json = """
            {
              "calibrationTicketId": "34",
              "calibrationSize": 1,
              "fieldName": "Estimate",
              "scale": [1, 2, 4, 8]
            }
            """;

        var config = JsonSerializer.Deserialize<EstimationConfig>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(config);
        Assert.Equal("34", config!.CalibrationTicketId);
        Assert.Equal(1, config.CalibrationSize);
        Assert.Equal("Estimate", config.FieldName);
        Assert.Equal(new List<int> { 1, 2, 4, 8 }, config.Scale);
    }
}

/// <summary>
/// Integration-style tests for the estimation step in AgentRunner:
/// - Calibration ticket is fetched and included in context
/// - Estimate from a step result is captured and added to template context
/// - SetField transition action fires with the estimate value
/// </summary>
public class EstimationIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _worktreeBase;
    private readonly ITaskBoardClient _boardClient;
    private readonly TaskFileManager _taskFileManager;
    private readonly GitWorkspaceManager _gitWorkspaceManager;

    private const string BoardId = "board-1";
    private const string TargetCardId = "37";
    private const string CalibrationCardId = "34";
    private const string DesignListId = "Ready for Design";

    public EstimationIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "est-int-" + Guid.NewGuid().ToString("N")[..8]);
        _worktreeBase = _tempDir + "-worktrees";
        Directory.CreateDirectory(_tempDir);
        InitGitRepo(_tempDir);

        _boardClient = Substitute.For<ITaskBoardClient>();
        _taskFileManager = new TaskFileManager(NullLogger<TaskFileManager>.Instance);
        _gitWorkspaceManager = new GitWorkspaceManager(NullLogger<GitWorkspaceManager>.Instance);
    }

    public void Dispose()
    {
        CleanupDirectory(_worktreeBase);
        try { RunGitSync(_tempDir, "worktree", "prune"); } catch { }
        CleanupDirectory(_tempDir);
    }

    [Fact]
    public async Task ExecuteAsync_EstimationConfigured_FetchesCalibrationTicket()
    {
        // Arrange: calibration ticket not already in board cards
        var boardCards = new List<BoardCard>
        {
            new(TargetCardId, "Estimate Tickets", "Design content here", DesignListId),
        };
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(boardCards);
        _boardClient.GetCardAsync(CalibrationCardId, Arg.Any<CancellationToken>())
            .Returns(new BoardCard(CalibrationCardId, "Calibration Ticket", "Small task body", "Done"));
        _boardClient.GetCardCommentsAsync(TargetCardId, Arg.Any<CancellationToken>())
            .Returns(new List<CardComment>());

        var stub = new EstimateCapturingExecutor(estimate: 4.0);
        var runner = CreateRunner(stub, withEstimation: true);

        // Act
        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: calibration ticket was fetched
        await _boardClient.Received(1).GetCardAsync(CalibrationCardId, Arg.Any<CancellationToken>());
        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_EstimationConfigured_EstimateWrittenToField()
    {
        // Arrange
        var boardCards = new List<BoardCard>
        {
            new(TargetCardId, "Estimate Tickets", "Design content here", DesignListId),
            new(CalibrationCardId, "Calibration Ticket", "Small task body", "Done"),
        };
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(boardCards);
        _boardClient.GetCardCommentsAsync(TargetCardId, Arg.Any<CancellationToken>())
            .Returns(new List<CardComment>());

        var stub = new EstimateCapturingExecutor(estimate: 4.0);
        var runner = CreateRunner(stub, withEstimation: true);

        // Act
        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // Assert: SetFieldAsync called with "4" for the Estimate field
        await _boardClient.Received(1).SetFieldAsync(
            TargetCardId, "Estimate", "4", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_EstimationConfigured_CalibrationAlreadyInContext_NotFetchedAgain()
    {
        // Calibration ticket is already among the board cards — should not trigger GetCardAsync for it
        var boardCards = new List<BoardCard>
        {
            new(TargetCardId, "Estimate Tickets", "Design content here", DesignListId),
            new(CalibrationCardId, "Calibration Ticket", "Small task body", "Done"),
        };
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(boardCards);
        _boardClient.GetCardCommentsAsync(TargetCardId, Arg.Any<CancellationToken>())
            .Returns(new List<CardComment>());

        var stub = new EstimateCapturingExecutor(estimate: 2.0);
        var runner = CreateRunner(stub, withEstimation: true);

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // GetCardAsync should NOT be called for the calibration ticket since it was in board cards
        await _boardClient.DidNotReceive().GetCardAsync(CalibrationCardId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_NoEstimationConfig_SetFieldNeverCalled()
    {
        // Arrange: no estimation config
        var boardCards = new List<BoardCard>
        {
            new(TargetCardId, "Estimate Tickets", "Design content here", DesignListId),
        };
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(boardCards);
        _boardClient.GetCardCommentsAsync(TargetCardId, Arg.Any<CancellationToken>())
            .Returns(new List<CardComment>());

        var stub = new EstimateCapturingExecutor(estimate: 4.0);
        var runner = CreateRunner(stub, withEstimation: false);

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // SetFieldAsync should not be called when no estimation config
        await _boardClient.DidNotReceive().SetFieldAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_EstimationConfigured_CalibrationFetchFails_StillCompletes()
    {
        // Arrange: calibration ticket fetch throws
        var boardCards = new List<BoardCard>
        {
            new(TargetCardId, "Estimate Tickets", "Design content here", DesignListId),
        };
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(boardCards);
        _boardClient.GetCardAsync(CalibrationCardId, Arg.Any<CancellationToken>())
            .Returns<BoardCard>(_ => throw new InvalidOperationException("Card not found"));
        _boardClient.GetCardCommentsAsync(TargetCardId, Arg.Any<CancellationToken>())
            .Returns(new List<CardComment>());

        var stub = new EstimateCapturingExecutor(estimate: 2.0);
        var runner = CreateRunner(stub, withEstimation: true);

        // Act: should still complete, not throw
        var result = await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        Assert.Equal(AgentOutcome.COMPLETE, result.Outcome);
    }

    [Fact]
    public async Task ExecuteAsync_EstimationStep_PromptIncludesCalibrationPlaceholders()
    {
        // Use a capturing executor to verify the prompt includes estimation context
        var boardCards = new List<BoardCard>
        {
            new(TargetCardId, "Estimate Tickets", "Design content here", DesignListId),
            new(CalibrationCardId, "Calibration Ticket", "Small task body", "Done"),
        };
        _boardClient.GetBoardCardsAsync(BoardId, Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns(boardCards);
        _boardClient.GetCardCommentsAsync(TargetCardId, Arg.Any<CancellationToken>())
            .Returns(new List<CardComment>());

        var capturingExecutor = new MultiStepCapturingExecutor();
        var runner = CreateRunner(capturingExecutor, withEstimation: true);

        await runner.ExecuteAsync(TargetCardId, BoardId, _tempDir, CancellationToken.None);

        // The estimation step prompt (step name "estimate_ticket") should contain calibration values
        var estimatePrompt = capturingExecutor.GetPromptForStep("estimate_ticket");
        Assert.NotNull(estimatePrompt);
        Assert.Contains("34", estimatePrompt); // CalibrationTicketId
        Assert.Contains("1", estimatePrompt);  // CalibrationSize
        Assert.Contains("1, 2, 4, 8", estimatePrompt); // EstimationScale
    }

    private AgentRunner CreateRunner(IAgentExecutor executor, bool withEstimation)
    {
        var config = BuildConfig(withEstimation);
        return new AgentRunner(
            _boardClient,
            AgentExecutorResolver.ForSingleExecutor(executor),
            _taskFileManager,
            _gitWorkspaceManager,
            config,
            new StubCrossReferenceResolver(),
            new AgentIdentity("Test", "Agent", "TestMachine"),
            new UpdateFileProcessor(_boardClient, config, new AgentIdentity("Test", "Agent", "TestMachine"), NullLogger<UpdateFileProcessor>.Instance),
            NullRunStore.Instance,
            new ImageDownloader(Substitute.For<IHttpClientFactory>(), NullLogger<ImageDownloader>.Instance),
            NullLogger<AgentRunner>.Instance);
    }

    private WorkflowConfig BuildConfig(bool withEstimation)
    {
        // Write prompt files to temp dir so AgentRunner can find them
        var promptDir = Path.Combine(_tempDir, "prompts", "states", "steps");
        Directory.CreateDirectory(promptDir);

        var designPrompt = Path.Combine(promptDir, "create_design.md");
        File.WriteAllText(designPrompt, "Design {TaskName} ({TaskId})");

        var estimatePrompt = Path.Combine(promptDir, "estimate_ticket.md");
        File.WriteAllText(estimatePrompt,
            "Estimate {TaskName} ({TaskId}). Calibration: #{CalibrationTicketId} at {CalibrationSize} pts. Scale: {EstimationScale}.");

        var systemPromptDir = Path.Combine(_tempDir, "prompts");
        Directory.CreateDirectory(systemPromptDir);
        File.WriteAllText(Path.Combine(systemPromptDir, "senior_engineer.md"), "You are a senior engineer.");
        File.WriteAllText(Path.Combine(systemPromptDir, "estimator.md"), "You are an estimator.");

        var steps = withEstimation
            ? new List<WorkflowStep>
            {
                new("create_design", "senior_engineer", TaskPromptFile: "prompts/states/steps/create_design.md"),
                new("estimate_ticket", "estimator", TaskPromptFile: "prompts/states/steps/estimate_ticket.md"),
            }
            : new List<WorkflowStep>
            {
                new("create_design", "senior_engineer", TaskPromptFile: "prompts/states/steps/create_design.md"),
            };

        var transitions = new Dictionary<string, TransitionTarget>
        {
            ["IN_PROGRESS"] = TransitionTarget.ForColumn("Designing"),
            ["NEEDS_INFO"] = TransitionTarget.ForColumn("Design Questions"),
            ["ERROR"] = TransitionTarget.ForColumn("Error"),
        };

        if (withEstimation)
        {
            transitions["COMPLETE"] = new TransitionTarget(
            [
                new TransitionAction(ActionTypes.MoveToColumn, "Designed"),
                new TransitionAction(ActionTypes.SetField, "{{estimation}}", Field: "Estimate"),
            ]);
        }
        else
        {
            transitions["COMPLETE"] = TransitionTarget.ForColumn("Designed");
        }

        var roles = new Dictionary<string, WorkflowRole>
        {
            ["senior_engineer"] = new("claude-opus-4-6",
                "You are a senior engineer.", new List<string>(),
                SystemPromptFile: "prompts/senior_engineer.md"),
            ["estimator"] = new("claude-haiku-4-5-20251001",
                "You are an estimator.", new List<string>(),
                SystemPromptFile: "prompts/estimator.md"),
        };

        var state = new WorkflowState(
            "Ready for Design", null, "agent_run", null, transitions,
            GitBehavior: "discard",
            Steps: steps);

        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState> { [DesignListId] = state },
            Roles: roles,
            Estimation: withEstimation
                ? new EstimationConfig(CalibrationCardId, 1, "Estimate", [1, 2, 4, 8])
                : null)
        {
            ConfigDirectory = _tempDir
        };

        return config.Normalised();
    }

    private static void InitGitRepo(string path)
    {
        RunGitSync(path, "init");
        RunGitSync(path, "config", "user.email", "test@test.com");
        RunGitSync(path, "config", "user.name", "Test");
        File.WriteAllText(Path.Combine(path, ".gitignore"), ".aiboard/\n");
        File.WriteAllText(Path.Combine(path, ".gitkeep"), "");
        RunGitSync(path, "add", ".");
        RunGitSync(path, "commit", "-m", "initial");
    }

    private static void CleanupDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var attrs = File.GetAttributes(file);
            if ((attrs & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);
        }
        Directory.Delete(path, recursive: true);
    }
}

/// <summary>
/// Agent executor that returns COMPLETE with a configurable estimate value.
/// Writes minimal content to the task file so AgentRunner can update the card body.
/// </summary>
internal sealed class EstimateCapturingExecutor : IAgentExecutor
{
    private readonly double? _estimate;

    public EstimateCapturingExecutor(double? estimate) => _estimate = estimate;

    public async Task<AgentResult> ExecuteAsync(
        AgentExecutionContext context, CancellationToken cancellationToken)
    {
        var taskFilePath = TaskBoard.Worker.Processing.TaskFileManager.GetTaskFilePath(
            context.WorkspacePath, context.TargetCardId, context.TargetCardTitle);

        if (File.Exists(taskFilePath))
        {
            var existing = await File.ReadAllTextAsync(taskFilePath, cancellationToken);
            await File.WriteAllTextAsync(taskFilePath, existing + "\n\n## Design\n\nStub content.", cancellationToken);
        }

        return new AgentResult(AgentOutcome.COMPLETE, "Stub complete", Estimate: _estimate);
    }
}

/// <summary>
/// Agent executor that captures prompts per step name for assertion.
/// Infers step from the task prompt content (relies on step-specific placeholder substitution).
/// </summary>
internal sealed class MultiStepCapturingExecutor : IAgentExecutor
{
    private readonly Dictionary<string, string> _promptsByStep = [];
    private int _callCount;

    /// <summary>Step name → task prompt, keyed by prompt content heuristic.</summary>
    public string? GetPromptForStep(string stepName)
    {
        _promptsByStep.TryGetValue(stepName, out var prompt);
        return prompt;
    }

    public async Task<AgentResult> ExecuteAsync(
        AgentExecutionContext context, CancellationToken cancellationToken)
    {
        var stepIndex = _callCount++;

        // Write task file content so AgentRunner can update card body
        var taskFilePath = TaskBoard.Worker.Processing.TaskFileManager.GetTaskFilePath(
            context.WorkspacePath, context.TargetCardId, context.TargetCardTitle);
        if (File.Exists(taskFilePath))
        {
            var existing = await File.ReadAllTextAsync(taskFilePath, cancellationToken);
            await File.WriteAllTextAsync(taskFilePath, existing + "\n\n## Step Output\n\nStub.", cancellationToken);
        }

        // Heuristic: estimation step prompt contains "Calibration"
        if (context.TaskPrompt.Contains("Calibration"))
            _promptsByStep["estimate_ticket"] = context.TaskPrompt;
        else
            _promptsByStep[$"step_{stepIndex}"] = context.TaskPrompt;

        // Last call returns an estimate; earlier calls return no estimate
        double? estimate = context.TaskPrompt.Contains("Calibration") ? 4.0 : null;
        return new AgentResult(AgentOutcome.COMPLETE, "Stub", Estimate: estimate);
    }
}
