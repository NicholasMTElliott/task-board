using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using Microsoft.Extensions.Options;
using TaskBoard.Worker;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

// ── 0. Help check (before any host building) ────────────────────────────────
if (CliDefinitions.ShouldShowHelp(args))
{
    CliDefinitions.PrintHelp();
    return;
}

// ── 1. Pre-parse values needed before the config pipeline is built ───────────
var configFilePath = PreParseArg(args, "--config");
var promptRootArg = PreParseArg(args, "--prompt-root");

// ── 2. Build host with layered configuration ─────────────────────────────────
//  Built-in order from CreateApplicationBuilder:
//    appsettings.json -> appsettings.{env}.json -> env vars -> command-line
//  We layer on top in ascending precedence:
var builder = Host.CreateApplicationBuilder(args);

if (configFilePath is not null)
    builder.Configuration.AddJsonFile(CliDefinitions.ResolvePath(configFilePath), optional: false, reloadOnChange: false);

builder.Configuration.AddJsonFile("appsettings.user.json", optional: true, reloadOnChange: false);

// Re-add env vars so they beat --config and appsettings.user.json
builder.Configuration.AddEnvironmentVariables();

// CLI args via switch mappings — highest precedence
builder.Configuration.AddCommandLine(args, CliDefinitions.SwitchMappings);

// ── 3. Resolve prompt base directory ─────────────────────────────────────────
var promptBaseDir = promptRootArg is not null
    ? CliDefinitions.ResolvePath(promptRootArg)
    : AppContext.BaseDirectory;

// ── 4. Resolve workflow config path from merged configuration ────────────────
var workflowPathRaw = builder.Configuration["WorkflowConfigPath"];
var workflowPath = string.IsNullOrEmpty(workflowPathRaw)
    ? Path.Combine(AppContext.BaseDirectory, "workflow.v1.json")
    : CliDefinitions.ResolvePath(workflowPathRaw);

builder.Services.AddSingleton<WorkflowConfig>(serviceProvider =>
{
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("WorkflowConfig");

    if (!File.Exists(workflowPath))
    {
        throw new InvalidOperationException(
            $"Workflow config not found at '{workflowPath}'. Set WorkflowConfigPath in appsettings.json, env var, or --workflow-config.");
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

    // Set prompt resolution base directory
    config.ConfigDirectory = promptBaseDir;

    return config;
});

// ── 5. Board provider selection ──────────────────────────────────────────────
var boardProvider = builder.Configuration["BoardProvider"]?.ToLowerInvariant() ?? "stub";

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

// ── 6. Agent executor selection ──────────────────────────────────────────────
var agentExecutorMode = builder.Configuration["AgentExecutor"]?.ToLowerInvariant() ?? "stub";
if (agentExecutorMode == "claude-cli")
{
    builder.Services.Configure<ClaudeCliLlmOptions>(builder.Configuration.GetSection(ClaudeCliLlmOptions.SectionName));
    builder.Services.PostConfigure<ClaudeCliLlmOptions>(opts =>
    {
        opts.ExecutablePath = ClaudeCliResolver.Resolve(opts.ExecutablePath);
    });
    builder.Services.AddSingleton<IAgentExecutor, ClaudeAgentExecutor>();
}
else if (agentExecutorMode == "codex")
{
    builder.Services.Configure<CodexCliLlmOptions>(builder.Configuration.GetSection(CodexCliLlmOptions.SectionName));
    builder.Services.PostConfigure<CodexCliLlmOptions>(opts =>
    {
        opts.ExecutablePath = CodexCliResolver.Resolve(opts.ExecutablePath);
    });
    builder.Services.AddSingleton<IAgentExecutor, CodexAgentExecutor>();
}
else
{
    builder.Services.AddSingleton<IAgentExecutor, StubAgentExecutor>();
}

// Generate agent identity for this process instance
var agentIdentity = AgentIdentity.Generate();
builder.Services.AddSingleton(agentIdentity);

// Agent mode services
builder.Services.AddSingleton<TaskFileManager>();

var worktreeBaseRaw = builder.Configuration["WorktreeBasePath"];
var worktreeBasePath = worktreeBaseRaw is not null ? CliDefinitions.ResolvePath(worktreeBaseRaw) : null;
builder.Services.AddSingleton(sp =>
    new GitWorkspaceManager(sp.GetRequiredService<ILogger<GitWorkspaceManager>>(), worktreeBasePath));

builder.Services.AddSingleton<AgentRunner>();
builder.Services.AddSingleton<MergeRunner>();
builder.Services.AddSingleton<PollingRunner>();

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program");

// ── 7. Runtime prerequisite validation ───────────────────────────────────────
{
    var config = host.Services.GetRequiredService<WorkflowConfig>();

    GitHubProjectsOptions? ghOpts = boardProvider == "github"
        ? host.Services.GetRequiredService<IOptions<GitHubProjectsOptions>>().Value
        : null;
    TrelloClientOptions? trelloOpts = boardProvider is "trello" or "live"
        ? host.Services.GetRequiredService<IOptions<TrelloClientOptions>>().Value
        : null;

    string? claudeExePath = agentExecutorMode == "claude-cli"
        ? host.Services.GetRequiredService<IOptions<ClaudeCliLlmOptions>>().Value.ExecutablePath
        : null;

    string? codexExePath = agentExecutorMode == "codex"
        ? host.Services.GetRequiredService<IOptions<CodexCliLlmOptions>>().Value.ExecutablePath
        : null;

    var prereqErrors = await PrerequisiteValidator.ValidateAsync(
        config, boardProvider, agentExecutorMode,
        promptBaseDir, claudeExePath, ghOpts, trelloOpts, codexExePath: codexExePath);

    if (prereqErrors.Count > 0)
    {
        logger.LogError(
            "Prerequisite validation failed:\n{Errors}",
            string.Join("\n", prereqErrors));
        return;
    }

    logger.LogInformation("All prerequisites validated successfully");
}

logger.LogInformation("Agent identity: {AgentName}", agentIdentity.DisplayName);

// ── 8. Resolve shared runtime parameters from merged configuration ───────────
var boardId = builder.Configuration["BoardId"]
    ?? (boardProvider == "github" ? builder.Configuration["GitHubProjects:ProjectNumber"] : null);

var workspacePathRaw = builder.Configuration["AgentWorkspacePath"];
var workspacePath = workspacePathRaw is not null ? CliDefinitions.ResolvePath(workspacePathRaw) : null;

// ── 9. Mode dispatch ─────────────────────────────────────────────────────────
var mode = builder.Configuration["Mode"]?.ToLowerInvariant();

if (mode == "agent")
{
    var cardId = builder.Configuration["CardId"];
    if (string.IsNullOrWhiteSpace(cardId))
    {
        logger.LogError("--card-id is required for agent mode");
        return;
    }

    if (string.IsNullOrWhiteSpace(boardId))
    {
        logger.LogError("--board-id, BoardId, or GitHubProjects:ProjectNumber is required for agent mode");
        return;
    }

    if (string.IsNullOrWhiteSpace(workspacePath))
    {
        logger.LogError("--workspace or AgentWorkspacePath is required for agent mode");
        return;
    }

    using var scope = host.Services.CreateScope();

    logger.LogInformation("Agent mode: card={CardId} board={BoardId} workspace={Workspace}",
        cardId, boardId, workspacePath);

    // Determine dispatch: fetch card to check if it's in a system_merge state
    var boardClientInstance = scope.ServiceProvider.GetRequiredService<ITaskBoardClient>();
    var workflowConfigInstance = scope.ServiceProvider.GetRequiredService<WorkflowConfig>();
    var boardCards = await boardClientInstance.GetBoardCardsAsync(boardId, CancellationToken.None);
    var targetCard = boardCards.FirstOrDefault(c => c.Id == cardId);

    AgentRunResult result;
    if (targetCard is not null
        && workflowConfigInstance.States.TryGetValue(targetCard.ColumnId, out var cardState)
        && string.Equals(cardState.GateType, "system_merge", StringComparison.OrdinalIgnoreCase))
    {
        var mergeRunner = scope.ServiceProvider.GetRequiredService<MergeRunner>();
        result = await mergeRunner.ExecuteAsync(cardId, boardId, workspacePath, CancellationToken.None);
    }
    else
    {
        var agentRunner = scope.ServiceProvider.GetRequiredService<AgentRunner>();
        result = await agentRunner.ExecuteAsync(cardId, boardId, workspacePath, CancellationToken.None);
    }

    logger.LogInformation("Agent run complete: outcome={Outcome}, error={ErrorDetail}",
        result.Outcome, result.ErrorDetail ?? "(none)");
    return;
}

if (mode == "polling")
{
    if (string.IsNullOrWhiteSpace(boardId))
    {
        logger.LogError("--board-id, BoardId, or GitHubProjects:ProjectNumber is required for polling mode");
        return;
    }

    if (string.IsNullOrWhiteSpace(workspacePath))
    {
        logger.LogError("--workspace or AgentWorkspacePath is required for polling mode");
        return;
    }

    var pollIntervalStr = builder.Configuration["PollIntervalSeconds"];
    var pollInterval = TimeSpan.FromSeconds(
        int.TryParse(pollIntervalStr, out var secs) ? secs : 60);

    // Validate polling-specific config requirements
    var pollingErrors = WorkflowConfigValidator.Validate(
        host.Services.GetRequiredService<WorkflowConfig>(), validatePolling: true);
    if (pollingErrors.Count > 0)
    {
        logger.LogError("Workflow config polling validation failed:\n{Errors}",
            string.Join("\n", pollingErrors));
        return;
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    using var scope = host.Services.CreateScope();
    var pollingRunner = scope.ServiceProvider.GetRequiredService<PollingRunner>();

    logger.LogInformation(
        "Polling mode: board={BoardId} workspace={Workspace} interval={Interval}s",
        boardId, workspacePath, pollInterval.TotalSeconds);

    await using var sleepInhibitor = await SystemSleepInhibitor.CreateAsync(logger);
    await pollingRunner.RunAsync(boardId, workspacePath, pollInterval, cts.Token);
    return;
}

// No recognized mode — show help
logger.LogError("No valid --mode specified. Use --mode agent or --mode polling.");
CliDefinitions.PrintHelp();

// ── Helpers ──────────────────────────────────────────────────────────────────

static string? PreParseArg(string[] args, string key)
{
    var i = Array.FindIndex(args, a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));
    return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
}
