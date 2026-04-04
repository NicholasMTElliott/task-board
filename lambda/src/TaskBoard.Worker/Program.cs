using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
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

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

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

    // Re-validate after normalization to catch any issues introduced by the transform
    var postErrors = WorkflowConfigValidator.Validate(config);
    if (postErrors.Count > 0)
    {
        throw new InvalidOperationException(
            $"Workflow config post-normalization validation failed:\n{string.Join("\n", postErrors)}");
    }

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
        var trelloBaseUrl = builder.Configuration.GetSection("Trello")["BaseUrl"] ?? "https://api.trello.com";
        builder.Services.AddTransient(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<TrelloClientOptions>>().Value;
            return new TrelloClient.TrelloAuthHandler(opts) { InnerHandler = new HttpClientHandler() };
        });
        builder.Services.AddHttpClient<ITaskBoardClient, TrelloClient>(client =>
        {
            client.BaseAddress = new Uri(trelloBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler<TrelloClient.TrelloAuthHandler>();
        builder.Services.AddHttpClient<ICrossReferenceResolver, TrelloCrossReferenceResolver>(client =>
        {
            client.BaseAddress = new Uri(trelloBaseUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler<TrelloClient.TrelloAuthHandler>();
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
// AGENT_EXECUTOR=stub → all providers mapped to stub (testing/dev)
// Any other value (or unset) → production mode: real providers are auto-detected
var agentExecutorMode = builder.Configuration["AgentExecutor"]?.ToLowerInvariant() ?? "stub";

// Always register StubAgentExecutor (used in stub mode and tests)
builder.Services.AddSingleton<StubAgentExecutor>();

HashSet<string> detectedProviders;
if (agentExecutorMode == "stub")
{
    // Stub mode: all providers map to stub, all considered available
    detectedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude-cli", "codex", "stub" };
}
else
{
    detectedProviders = await PrerequisiteValidator.DetectAvailableProvidersAsync();

    if (detectedProviders.Contains("claude-cli"))
    {
        builder.Services.Configure<ClaudeCliLlmOptions>(builder.Configuration.GetSection(ClaudeCliLlmOptions.SectionName));
        builder.Services.PostConfigure<ClaudeCliLlmOptions>(opts =>
        {
            opts.ExecutablePath = ClaudeCliResolver.Resolve(opts.ExecutablePath);
        });
        builder.Services.AddSingleton<ClaudeAgentExecutor>();
    }

    if (detectedProviders.Contains("codex"))
    {
        builder.Services.Configure<CodexCliLlmOptions>(builder.Configuration.GetSection(CodexCliLlmOptions.SectionName));
        builder.Services.PostConfigure<CodexCliLlmOptions>(opts =>
        {
            opts.ExecutablePath = CodexCliResolver.Resolve(opts.ExecutablePath);
        });
        builder.Services.AddSingleton<CodexAgentExecutor>();
    }
}

builder.Services.AddSingleton<IAgentExecutorResolver>(sp =>
{
    if (agentExecutorMode == "stub")
    {
        return AgentExecutorResolver.ForSingleExecutor(
            sp.GetRequiredService<StubAgentExecutor>());
    }

    var executors = new Dictionary<string, IAgentExecutor>(StringComparer.OrdinalIgnoreCase);
    if (detectedProviders.Contains("claude-cli"))
        executors["claude-cli"] = sp.GetRequiredService<ClaudeAgentExecutor>();
    if (detectedProviders.Contains("codex"))
        executors["codex"] = sp.GetRequiredService<CodexAgentExecutor>();
    return new AgentExecutorResolver(executors);
});

// Generate agent identity for this process instance
var agentIdentity = AgentIdentity.Generate();
builder.Services.AddSingleton(agentIdentity);

// Agent mode services
builder.Services.AddSingleton<TaskFileManager>();

var worktreeBaseRaw = builder.Configuration["WorktreeBasePath"];
var worktreeBasePath = worktreeBaseRaw is not null ? CliDefinitions.ResolvePath(worktreeBaseRaw) : null;
var gitTimeoutSeconds = int.TryParse(builder.Configuration["GitTimeoutSeconds"], out var gts) ? gts : 30;
builder.Services.AddSingleton(sp =>
    new GitWorkspaceManager(sp.GetRequiredService<ILogger<GitWorkspaceManager>>(), worktreeBasePath, gitTimeoutSeconds));

builder.Services.AddSingleton<AgentRunner>();
builder.Services.AddSingleton<MergeRunner>();
builder.Services.AddSingleton<PollingRunner>();

// ── Queue-driven mode services (registered if Pgmq connection is configured) ──
var pgmqConnectionString = builder.Configuration.GetSection(PgmqOptions.SectionName)["ConnectionString"];
if (!string.IsNullOrWhiteSpace(pgmqConnectionString))
{
    builder.Services.Configure<PgmqOptions>(builder.Configuration.GetSection(PgmqOptions.SectionName));
    builder.Services.AddSingleton(NpgsqlDataSource.Create(pgmqConnectionString));
    builder.Services.AddSingleton<IPingQueueClient, PgmqPingQueueClient>();
    builder.Services.AddSingleton<ICardClaimService, CardClaimService>();
    builder.Services.AddSingleton<QueueDrivenRunner>();
}

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program");

// ── 7. Runtime prerequisite validation ───────────────────────────────────────
{
    // Warn if AGENT_EXECUTOR is set to a real provider name (now deprecated for provider selection)
    var rawAgentExec = builder.Configuration["AgentExecutor"];
    if (rawAgentExec is not null
        && !string.Equals(rawAgentExec, "stub", StringComparison.OrdinalIgnoreCase))
    {
        logger.LogWarning(
            "AGENT_EXECUTOR is set to '{Value}' but this value is no longer used for provider selection — " +
            "real providers are now auto-detected. Only 'stub' retains special meaning.",
            rawAgentExec);
    }

    var config = host.Services.GetRequiredService<WorkflowConfig>();

    GitHubProjectsOptions? ghOpts = boardProvider == "github"
        ? host.Services.GetRequiredService<IOptions<GitHubProjectsOptions>>().Value
        : null;
    TrelloClientOptions? trelloOpts = boardProvider is "trello" or "live"
        ? host.Services.GetRequiredService<IOptions<TrelloClientOptions>>().Value
        : null;

    var prereqErrors = await PrerequisiteValidator.ValidateAsync(
        config, boardProvider, detectedProviders,
        promptBaseDir, ghOpts, trelloOpts);

    if (prereqErrors.Count > 0)
    {
        logger.LogError(
            "Prerequisite validation failed:\n{Errors}",
            string.Join("\n", prereqErrors));
        return;
    }

    logger.LogInformation("All prerequisites validated successfully");
    logger.LogInformation("Available AI providers: {Providers}",
        string.Join(", ", detectedProviders.Where(p => p != "stub").Order()));
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
    var boardCards = await boardClientInstance.GetBoardCardsAsync(boardId, CancellationToken.None, workflowConfigInstance.GetTerminalStateNames());
    var targetCard = boardCards.FirstOrDefault(c => c.Id == cardId);

    AgentRunResult result;
    if (targetCard is not null
        && workflowConfigInstance.States.TryGetValue(targetCard.ColumnId, out var cardState)
        && string.Equals(cardState.GateType, GateTypes.SystemMerge, StringComparison.OrdinalIgnoreCase))
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
        int.TryParse(pollIntervalStr, out var secs) ? secs : 120);

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

// ── Mode: queue (webhook-driven via PGMQ pings queue) ──────────────────────
if (mode == "queue")
{
    if (string.IsNullOrWhiteSpace(boardId))
    {
        logger.LogError("--board-id is required for queue mode");
        return;
    }

    if (string.IsNullOrWhiteSpace(workspacePath))
    {
        logger.LogError("--workspace or AgentWorkspacePath is required for queue mode");
        return;
    }

    if (string.IsNullOrWhiteSpace(pgmqConnectionString))
    {
        logger.LogError("Pgmq:ConnectionString (or --neon-connection) is required for queue mode");
        return;
    }

    // Validate polling-specific config (queue mode reuses the same validation)
    var queueErrors = WorkflowConfigValidator.Validate(
        host.Services.GetRequiredService<WorkflowConfig>(), validatePolling: true);
    if (queueErrors.Count > 0)
    {
        logger.LogError("Workflow config validation failed:\n{Errors}",
            string.Join("\n", queueErrors));
        return;
    }

    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cts.Cancel();
    };

    using var scope = host.Services.CreateScope();
    var queueRunner = scope.ServiceProvider.GetRequiredService<QueueDrivenRunner>();

    logger.LogInformation(
        "Queue mode: board={BoardId} workspace={Workspace} queue={Queue}",
        boardId, workspacePath,
        builder.Configuration.GetSection(PgmqOptions.SectionName)["PingQueueName"] ?? "pings");

    await using var sleepInhibitor = await SystemSleepInhibitor.CreateAsync(logger);
    await queueRunner.RunAsync(boardId, workspacePath, cts.Token);
    return;
}

// No recognized mode — show help
logger.LogError("No valid --mode specified. Use --mode agent, --mode polling, or --mode queue.");
CliDefinitions.PrintHelp();

// ── Helpers ──────────────────────────────────────────────────────────────────

static string? PreParseArg(string[] args, string key)
{
    var i = Array.FindIndex(args, a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));
    return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
}
