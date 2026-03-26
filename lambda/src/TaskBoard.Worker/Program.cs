using System.Text.Json;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<WorkflowConfig>(serviceProvider =>
{
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("WorkflowConfig");
    var workflowPath = Environment.GetEnvironmentVariable("WORKFLOW_CONFIG_PATH")
        ?? Path.Combine(AppContext.BaseDirectory, "workflow.v1.json");

    if (!File.Exists(workflowPath))
    {
        throw new InvalidOperationException(
            $"Workflow config not found at '{workflowPath}'. Set WORKFLOW_CONFIG_PATH or ensure workflow.v1.json is in the output directory.");
    }

    logger.LogInformation("Loading workflow config from {WorkflowPath}", workflowPath);
    var workflowJson = File.ReadAllText(workflowPath);
    var config = JsonSerializer.Deserialize<WorkflowConfig>(workflowJson, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    }) ?? throw new InvalidOperationException("Failed to deserialize workflow config");

    var errors = WorkflowConfigValidator.Validate(config);
    if (errors.Count > 0)
    {
        throw new InvalidOperationException(
            $"Workflow config validation failed:\n{string.Join("\n", errors)}");
    }

    // Normalise legacy single-step states into canonical steps-based model
    config = config.Normalised();

    return config;
});

// Board provider selection
var boardProvider = Environment.GetEnvironmentVariable("BOARD_PROVIDER")?.ToLowerInvariant() ?? "stub";

switch (boardProvider)
{
    case "trello":
    case "live":
        builder.Services.Configure<TrelloClientOptions>(builder.Configuration.GetSection(TrelloClientOptions.SectionName));
        builder.Services.AddHttpClient<ITaskBoardClient, TrelloClient>(client =>
        {
            var baseUrl = builder.Configuration.GetSection("Trello")["BaseUrl"] ?? "https://api.trello.com";
            client.BaseAddress = new Uri(baseUrl);
        });
        builder.Services.AddHttpClient<ICrossReferenceResolver, TrelloCrossReferenceResolver>(client =>
        {
            var baseUrl = builder.Configuration.GetSection("Trello")["BaseUrl"] ?? "https://api.trello.com";
            client.BaseAddress = new Uri(baseUrl);
        });
        break;
    case "github":
        builder.Services.Configure<GitHubProjectsOptions>(builder.Configuration.GetSection(GitHubProjectsOptions.SectionName));
        builder.Services.AddSingleton<ITaskBoardClient, GitHubProjectsClient>();
        builder.Services.AddSingleton<ICrossReferenceResolver, GitHubCrossReferenceResolver>();
        break;
    default:
        builder.Services.AddSingleton<ITaskBoardClient, StubTaskBoardClient>();
        builder.Services.AddSingleton<ICrossReferenceResolver, StubCrossReferenceResolver>();
        break;
}

// Agent executor selection
var agentExecutorMode = Environment.GetEnvironmentVariable("AGENT_EXECUTOR")?.ToLowerInvariant() ?? "stub";
if (agentExecutorMode == "claude-cli")
{
    builder.Services.Configure<ClaudeCliLlmOptions>(builder.Configuration.GetSection(ClaudeCliLlmOptions.SectionName));
    builder.Services.AddSingleton<IAgentExecutor, ClaudeAgentExecutor>();
}
else
{
    builder.Services.AddSingleton<IAgentExecutor, StubAgentExecutor>();
}

// Agent mode services
builder.Services.AddSingleton<TaskFileManager>();

var worktreeBasePath = GetArgument(args, "--worktree-base")
    ?? Environment.GetEnvironmentVariable("WORKTREE_BASE_PATH");
builder.Services.AddSingleton(sp =>
    new GitWorkspaceManager(sp.GetRequiredService<ILogger<GitWorkspaceManager>>(), worktreeBasePath));

builder.Services.AddSingleton<AgentRunner>();
builder.Services.AddSingleton<PollingRunner>();

var app = builder.Build();

if (GetArgument(args, "--mode")?.ToLowerInvariant() == "agent")
{
    var cardId = GetArgument(args, "--card-id");
    if (string.IsNullOrWhiteSpace(cardId))
    {
        app.Logger.LogError("--card-id is required for agent mode");
        return;
    }

    var boardId = GetArgument(args, "--board-id")
        ?? Environment.GetEnvironmentVariable("BOARD_ID")
        ?? Environment.GetEnvironmentVariable("TRELLO_BOARD_ID");
    if (string.IsNullOrWhiteSpace(boardId))
    {
        app.Logger.LogError("--board-id or BOARD_ID is required for agent mode");
        return;
    }

    var workspacePath = GetArgument(args, "--workspace")
        ?? Environment.GetEnvironmentVariable("AGENT_WORKSPACE_PATH");
    if (string.IsNullOrWhiteSpace(workspacePath))
    {
        app.Logger.LogError("--workspace or AGENT_WORKSPACE_PATH is required for agent mode");
        return;
    }

    using var scope = app.Services.CreateScope();
    var agentRunner = scope.ServiceProvider.GetRequiredService<AgentRunner>();

    app.Logger.LogInformation("Agent mode: card={CardId} board={BoardId} workspace={Workspace}",
        cardId, boardId, workspacePath);

    var result = await agentRunner.ExecuteAsync(cardId, boardId, workspacePath, CancellationToken.None);

    app.Logger.LogInformation("Agent run complete: outcome={Outcome}, error={ErrorDetail}",
        result.Outcome, result.ErrorDetail ?? "(none)");
    return;
}

if (GetArgument(args, "--mode")?.ToLowerInvariant() == "polling")
{
    var boardId = GetArgument(args, "--board-id")
        ?? Environment.GetEnvironmentVariable("BOARD_ID")
        ?? Environment.GetEnvironmentVariable("TRELLO_BOARD_ID");
    if (string.IsNullOrWhiteSpace(boardId))
    {
        app.Logger.LogError("--board-id or BOARD_ID is required for polling mode");
        return;
    }

    var workspacePath = GetArgument(args, "--workspace")
        ?? Environment.GetEnvironmentVariable("AGENT_WORKSPACE_PATH");
    if (string.IsNullOrWhiteSpace(workspacePath))
    {
        app.Logger.LogError("--workspace or AGENT_WORKSPACE_PATH is required for polling mode");
        return;
    }

    var pollIntervalStr = GetArgument(args, "--poll-interval")
        ?? Environment.GetEnvironmentVariable("POLL_INTERVAL_SECONDS");
    var pollInterval = TimeSpan.FromSeconds(
        int.TryParse(pollIntervalStr, out var secs) ? secs : 60);

    // Validate polling-specific config requirements
    var pollingErrors = WorkflowConfigValidator.Validate(
        app.Services.GetRequiredService<WorkflowConfig>(), validatePolling: true);
    if (pollingErrors.Count > 0)
    {
        app.Logger.LogError("Workflow config polling validation failed:\n{Errors}",
            string.Join("\n", pollingErrors));
        return;
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    using var scope = app.Services.CreateScope();
    var pollingRunner = scope.ServiceProvider.GetRequiredService<PollingRunner>();

    app.Logger.LogInformation(
        "Polling mode: board={BoardId} workspace={Workspace} interval={Interval}s",
        boardId, workspacePath, pollInterval.TotalSeconds);

    await pollingRunner.RunAsync(boardId, workspacePath, pollInterval, cts.Token);
    return;
}

app.MapGet("/health", () => Results.Ok(new { ok = true, service = "task-board-lambda-worker" }));
app.Run();

static string? GetArgument(string[] args, string key)
{
    var index = Array.FindIndex(args, value => string.Equals(value, key, StringComparison.OrdinalIgnoreCase));
    if (index < 0 || index + 1 >= args.Length)
    {
        return null;
    }

    return args[index + 1];
}
