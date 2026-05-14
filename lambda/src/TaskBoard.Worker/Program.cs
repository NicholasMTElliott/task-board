using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using TaskBoard.Worker;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Configuration;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using TaskBoard.Worker.Validation;

// ── 0. Help check (before any host building) ────────────────────────────────
if (CliDefinitions.ShouldShowHelp(args))
{
    CliDefinitions.PrintHelp();
    return;
}

// Normalise bare boolean flags (e.g. --force, --non-interactive) into
// --name=true form so AddCommandLine binds them. Must happen BEFORE every
// downstream consumer (AddCommandLine, ValidateKnownFlags). All downstream
// code uses normalisedArgs instead of args.
var normalisedArgs = CliDefinitions.NormalizeBareBooleanFlags(args);

// ── 1. Pre-parse values needed before the config pipeline is built ───────────
var configFilePath = PreParseArg(normalisedArgs, "--config");
var promptRootArg = PreParseArg(normalisedArgs, "--prompt-root");

// ── 2. Build host with layered configuration ─────────────────────────────────
// Precedence (lowest → highest, later wins):
//   1. EXE-dir defaults     (shipped with the binary)
//   2. cwd/.aiboard/        (project-tracked config)
//   3. cwd/                 (local overrides — closest to invocation wins)
//   4. --config <path>      (explicit operator override)
//   5. environment variables
//   6. CLI args             (highest)
var builder = Host.CreateApplicationBuilder(args);

// Discard the default sources added by CreateApplicationBuilder so we can
// re-add JSON files in the precise order documented above. Env vars and CLI
// are re-added at the end so they remain top-precedence.
builder.Configuration.Sources.Clear();

// Track config sources for diagnostics when required values are missing.
var configSources = new List<string>();
void AddJsonLayer(string path)
{
    builder.Configuration.AddJsonFile(path, optional: true, reloadOnChange: false);
    configSources.Add($"  json   {path}  [{(File.Exists(path) ? "exists" : "missing")}]");
}

builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

var envName = builder.Environment.EnvironmentName;
var exeDir = AppContext.BaseDirectory;
var cwd = Directory.GetCurrentDirectory();
var cwdIsExeDir = string.Equals(
    Path.GetFullPath(cwd), Path.GetFullPath(exeDir), StringComparison.OrdinalIgnoreCase);

// Layer 1 — EXE-dir defaults (lowest precedence)
AddJsonLayer(Path.Combine(exeDir, "appsettings.json"));
AddJsonLayer(Path.Combine(exeDir, $"appsettings.{envName}.json"));
AddJsonLayer(Path.Combine(exeDir, "appsettings.user.json"));

// Layer 2 — project-tracked config under cwd/.aiboard/
var cwdAiboardDir = Path.Combine(cwd, ".aiboard");
AddJsonLayer(Path.Combine(cwdAiboardDir, "appsettings.json"));
AddJsonLayer(Path.Combine(cwdAiboardDir, "appsettings.user.json"));

// Layer 3 — local cwd overrides (skip when cwd == exeDir to avoid loading the
// same file twice and confusing the diagnostic output).
if (!cwdIsExeDir)
{
    AddJsonLayer(Path.Combine(cwd, "appsettings.json"));
    AddJsonLayer(Path.Combine(cwd, $"appsettings.{envName}.json"));
    AddJsonLayer(Path.Combine(cwd, "appsettings.user.json"));
}

// Layer 4 — explicit operator override via --config (required if specified)
if (configFilePath is not null)
{
    var resolvedConfig = CliDefinitions.ResolvePath(configFilePath);
    builder.Configuration.AddJsonFile(resolvedConfig, optional: false, reloadOnChange: false);
    configSources.Add($"  json   {resolvedConfig}  [--config; required]");
}

// Layer 5 — env vars
builder.Configuration.AddEnvironmentVariables();
configSources.Add("  env    environment variables  [checked]");

// Layer 6 — CLI args (highest precedence)
builder.Configuration.AddCommandLine(normalisedArgs, CliDefinitions.SwitchMappings);
configSources.Add($"  cli    command-line arguments  [{(args.Length == 0 ? "none provided" : $"{args.Length} arg(s)")}]");

// Reject unknown CLI flags loudly. AddCommandLine silently ignores anything
// that doesn't match SwitchMappings, which means a typo like `--validate`
// falls through to whatever Mode is set in appsettings.user.json. example-project hit
// exactly this on v0.0.15 (typed --validate, ran in polling mode). Bail
// before doing anything else so the operator sees the typo and can fix it.
{
    var report = CliDefinitions.ValidateKnownFlags(normalisedArgs);
    if (report.UnknownFlags.Count > 0)
    {
        Console.Error.WriteLine(
            $"Unknown CLI flag(s): {string.Join(", ", report.UnknownFlags)}.");
        foreach (var (typo, suggestion) in report.TypoHints)
            Console.Error.WriteLine($"  Did you mean '{suggestion}' instead of '{typo}'?");
        Console.Error.WriteLine("Run 'aiboard --help' for the full list of recognised flags.");
        Environment.ExitCode = 1;
        return;
    }
}

// ── --install: install bundled Claude Code skills (early, before host build) ─
// Runs first when --install is set so `aiboard --install --mode <X>` installs
// the skill and then continues with the requested mode. When --install is the
// only argument (no Mode resolved from merged config), exits cleanly with 0.
// On failure, exits with the runner's non-zero exit code without continuing.
{
    var installRequested = string.Equals(
        builder.Configuration["Install"]?.Trim(), "true", StringComparison.OrdinalIgnoreCase);
    if (installRequested)
    {
        using var installLoggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        }));
        var installLogger = installLoggerFactory.CreateLogger("Install");
        var installRunner = new InstallRunner(installLogger);
        var installExit = await installRunner.RunAsync(CancellationToken.None);
        if (installExit != 0)
        {
            Environment.ExitCode = installExit;
            return;
        }

        // Standalone install (no --mode resolved from any config layer) → exit
        // cleanly. Otherwise fall through and continue with the requested mode.
        var requestedMode = builder.Configuration["Mode"]?.Trim();
        if (string.IsNullOrEmpty(requestedMode))
        {
            return;
        }
    }
}

// ── Mode: init (early bail-out before host build) ────────────────────────────
// Init scaffolds .aiboard/ in the cwd and exits. Runs before WorkflowConfig is
// registered (no workflow.json yet) and before provider/executor probing (no
// reason to validate Docker/postgres for a scaffold-only run).
{
    var initMode = builder.Configuration["Mode"]?.Trim();
    if (string.Equals(initMode, "init", StringComparison.OrdinalIgnoreCase))
    {
        using var initLoggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
        {
            o.SingleLine = true;
            o.TimestampFormat = "HH:mm:ss ";
        }));
        var initLogger = initLoggerFactory.CreateLogger("Init");
        var initRunner = new InitRunner(
            builder.Configuration, initLogger,
            runExternal: InitRunner.RunProcessAsync);
        var initExit = await initRunner.RunAsync(CancellationToken.None);
        Environment.ExitCode = initExit;
        return;
    }
}

// ── 3. Resolve prompt base directory ─────────────────────────────────────────
var promptBaseDir = promptRootArg is not null
    ? CliDefinitions.ResolvePath(promptRootArg)
    : AppContext.BaseDirectory;

// ── 4. Resolve workflow config path from merged configuration ────────────────
var workflowPathRaw = builder.Configuration["WorkflowConfigPath"];
string? workflowPath;
string[] workflowProbed;
if (!string.IsNullOrEmpty(workflowPathRaw))
{
    workflowPath = CliDefinitions.ResolvePath(workflowPathRaw);
    workflowProbed = new[] { workflowPath };
}
else
{
    workflowProbed = new[]
    {
        Path.Combine(Directory.GetCurrentDirectory(), ".aiboard", "workflow.json"),
        Path.Combine(AppContext.BaseDirectory, "workflow.json"),
        Path.Combine(AppContext.BaseDirectory, "workflow.v1.json"),
    };
    workflowPath = Array.Find(workflowProbed, File.Exists);
}

builder.Services.AddSingleton<WorkflowConfig>(serviceProvider =>
{
    var logger = serviceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("WorkflowConfig");

    if (workflowPath is null || !File.Exists(workflowPath))
    {
        throw new InvalidOperationException(
            "Workflow config not found. Probed locations:\n  " +
            string.Join("\n  ", workflowProbed) +
            "\nSet WorkflowConfigPath in appsettings.json, env var, or --workflow-config.");
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

    // Non-fatal audit — surface configurations that are valid but may not match
    // operator intent (e.g. Codex role without an explicit sandbox policy).
    foreach (var warning in WorkflowConfigValidator.Audit(config))
    {
        logger.LogWarning("Workflow audit: {Warning}", warning);
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
        builder.Services.AddSingleton<ICardDependencyClient, GitHubIssueDependencyClient>();
        builder.Services.AddSingleton<IBoardShapeProbe, GitHubProjectShapeProbe>();
        builder.Services.AddSingleton<IBoardShapeApplier, GitHubProjectShapeApplier>();
        break;
    default:
        builder.Services.AddSingleton<ITaskBoardClient, StubTaskBoardClient>();
        builder.Services.AddSingleton<ICrossReferenceResolver, StubCrossReferenceResolver>();
        break;
}

// Default probe for providers without a dedicated implementation (stub, trello)
builder.Services.TryAddSingleton<ICardDependencyClient>(NullCardDependencyClient.Instance);
builder.Services.TryAddSingleton<IBoardShapeProbe, NullBoardShapeProbe>();
builder.Services.TryAddSingleton<IBoardShapeApplier, NullBoardShapeApplier>();

// Docker image probe — used by ValidationRunner to warn about missing sandbox
// images. Always registers the real probe; if Docker isn't available it
// surfaces a per-image ProbeError that ValidationRunner converts to an Info
// finding (the whole probe pass is wrapped in try/catch as well).
builder.Services.AddSingleton<IDockerImageProbe, DockerImageProbe>();

// ── 6. Agent executor selection ──────────────────────────────────────────────
// AGENT_EXECUTOR=stub              → all providers mapped to stub (testing/dev)
// AGENT_EXECUTOR=docker-claude-cli → Docker executor registered under both
//                                    "docker-claude-cli" and "claude-cli" keys
//                                    (transparent substitution; fails if Docker unavailable)
// AGENT_EXECUTOR=docker-opencode   → OpenCode-in-Docker executor registered
//                                    under "docker-opencode" (distinct provider key;
//                                    no aliasing, fails if Docker unavailable)
// AGENT_EXECUTOR=docker-claude-qwen→ Claude-CLI-in-Docker pointed at the local
//                                    llama.cpp proxy (Qwen3.6) registered under
//                                    "docker-claude-qwen". Distinct from docker-opencode
//                                    so both Qwen-target executors can be A/B'd via
//                                    candidate evaluation. Fails if Docker unavailable.
// AGENT_EXECUTOR=docker-codex      → Codex-CLI-in-Docker registered under
//                                    "docker-codex" (sandboxed Codex with --yolo;
//                                    container is the security boundary). Fails
//                                    if Docker unavailable.
// Any other value (or unset)       → production mode: real providers are auto-detected
var agentExecutorMode = builder.Configuration["AgentExecutor"]?.ToLowerInvariant() ?? "stub";
var dockerModeRequested = agentExecutorMode == "docker-claude-cli";
var openCodeModeRequested = agentExecutorMode == "docker-opencode";
var claudeQwenModeRequested = agentExecutorMode == "docker-claude-qwen";
var codexDockerModeRequested = agentExecutorMode == "docker-codex";

// --unsafe gating: by default, host CLI executors (claude-cli, codex) are NOT
// registered. Only the sandboxed (docker-*) executors are available.
// Operators must opt in via --unsafe (or "Unsafe": true in config) to permit
// host-CLI execution, which runs the agent with full filesystem and credential
// access on the host. Workflows referencing claude-cli or codex without
// --unsafe fail at startup with a clear migration message.
var unsafeMode = string.Equals(
    builder.Configuration["Unsafe"], "true", StringComparison.OrdinalIgnoreCase);

// Always register StubAgentExecutor (used in stub mode and tests)
builder.Services.AddSingleton<StubAgentExecutor>();

HashSet<string> detectedProviders;
if (agentExecutorMode == "stub")
{
    // Stub mode: all providers map to stub, all considered available
    detectedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "claude-cli", "codex", "stub", "docker-claude-cli", "docker-codex", "docker-opencode", "docker-claude-qwen" };
}
else
{
    detectedProviders = await PrerequisiteValidator.DetectAvailableProvidersAsync();

    // Host CLI executors (claude-cli, codex) require --unsafe. Without it,
    // they're filtered out of detectedProviders here so the workflow-vs-providers
    // cross-check below produces a clear error if the workflow references them.
    // Docker-wrapped executors are always available (when Docker itself is detected).
    if (!unsafeMode)
    {
        detectedProviders.Remove("claude-cli");
        detectedProviders.Remove("codex");
    }

    // In docker-claude-cli mode, claude-cli is intentionally routed through Docker —
    // do not register ClaudeAgentExecutor for direct (non-Docker) use.
    // Also requires --unsafe (host CLI execution).
    if (detectedProviders.Contains("claude-cli") && !dockerModeRequested && unsafeMode)
    {
        builder.Services.Configure<ClaudeCliLlmOptions>(builder.Configuration.GetSection(ClaudeCliLlmOptions.SectionName));
        builder.Services.PostConfigure<ClaudeCliLlmOptions>(opts =>
        {
            opts.ExecutablePath = ClaudeCliResolver.Resolve(opts.ExecutablePath);
        });
        builder.Services.AddSingleton<ClaudeAgentExecutor>();
    }

    if (detectedProviders.Contains("codex") && unsafeMode)
    {
        builder.Services.Configure<CodexCliLlmOptions>(builder.Configuration.GetSection(CodexCliLlmOptions.SectionName));
        builder.Services.PostConfigure<CodexCliLlmOptions>(opts =>
        {
            opts.ExecutablePath = CodexCliResolver.Resolve(opts.ExecutablePath);
        });
        builder.Services.AddSingleton<CodexAgentExecutor>();
    }

    if (detectedProviders.Contains("docker") || dockerModeRequested)
    {
        // Ensure ClaudeCliLlmOptions is available for Docker-only environments
        // (budget/timeout config is shared with the containerized claude invocation)
        if (dockerModeRequested && !detectedProviders.Contains("claude-cli"))
        {
            builder.Services.Configure<ClaudeCliLlmOptions>(builder.Configuration.GetSection(ClaudeCliLlmOptions.SectionName));
        }

        // Bind legacy `Docker` section first (deprecated; provides defaults when set),
        // then the new `DockerAgents:Claude` section on top so new values win on conflict.
        builder.Services.Configure<DockerClaudeAgentOptions>(
            builder.Configuration.GetSection(DockerClaudeAgentOptions.LegacySectionName));
        builder.Services.Configure<DockerClaudeAgentOptions>(
            builder.Configuration.GetSection(DockerClaudeAgentOptions.SectionName));
        builder.Services.PostConfigure<DockerClaudeAgentOptions>(opts =>
        {
            if (string.IsNullOrEmpty(opts.CredentialPath))
            {
                var credPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
                if (Directory.Exists(credPath))
                    opts.CredentialPath = credPath;
            }
        });
        builder.Services.AddSingleton<DockerClaudeAgentExecutor>();

        // Only mark docker-claude-cli as available when Docker is actually detected.
        // If dockerModeRequested but docker is absent, the fail-fast check below fires.
        if (dockerModeRequested && detectedProviders.Contains("docker"))
            detectedProviders.Add("docker-claude-cli");
    }

    // OpenCode-in-Docker executor (provider key: docker-opencode).
    // Registered whenever Docker is available so workflow configs can route to it.
    // If AGENT_EXECUTOR=docker-opencode is explicitly requested but Docker is
    // unavailable, the fail-fast check below will fire.
    if (detectedProviders.Contains("docker") || openCodeModeRequested)
    {
        builder.Services.Configure<DockerOpenCodeAgentOptions>(
            builder.Configuration.GetSection(DockerOpenCodeAgentOptions.SectionName));
        builder.Services.AddSingleton<DockerOpenCodeMountBuilder>();
        builder.Services.AddSingleton<DockerOpenCodeAgentExecutor>();

        if (detectedProviders.Contains("docker"))
            detectedProviders.Add("docker-opencode");
    }

    // Claude-CLI-on-Qwen executor (provider key: docker-claude-qwen).
    // Same Claude CLI binary as docker-claude-cli but with the endpoint
    // redirected to the local llama-server proxy via env vars + a synthetic
    // ~/.claude. Lives alongside docker-opencode so both Qwen-target executors
    // can be candidate-evaluated against each other.
    if (detectedProviders.Contains("docker") || claudeQwenModeRequested)
    {
        builder.Services.Configure<DockerClaudeQwenAgentOptions>(
            builder.Configuration.GetSection(DockerClaudeQwenAgentOptions.SectionName));
        builder.Services.AddSingleton<DockerClaudeQwenMountBuilder>();
        builder.Services.AddSingleton<DockerClaudeQwenAgentExecutor>();

        if (detectedProviders.Contains("docker"))
            detectedProviders.Add("docker-claude-qwen");
    }

    // Codex-CLI-in-Docker executor (provider key: docker-codex). Sandboxed
    // Codex with --yolo by default — the container itself provides the
    // filesystem isolation that --yolo would normally bypass. Auth comes from
    // the host's ~/.codex/ directory copied into a per-run staging dir.
    if (detectedProviders.Contains("docker") || codexDockerModeRequested)
    {
        builder.Services.Configure<DockerCodexAgentOptions>(
            builder.Configuration.GetSection(DockerCodexAgentOptions.SectionName));
        builder.Services.PostConfigure<DockerCodexAgentOptions>(opts =>
        {
            if (string.IsNullOrEmpty(opts.CredentialPath))
            {
                var credPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
                if (Directory.Exists(credPath))
                    opts.CredentialPath = credPath;
            }
        });
        builder.Services.AddSingleton<DockerCodexMountBuilder>();
        builder.Services.AddSingleton<DockerCodexAgentExecutor>();

        if (detectedProviders.Contains("docker"))
            detectedProviders.Add("docker-codex");
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

    if (detectedProviders.Contains("claude-cli") && !dockerModeRequested && unsafeMode)
        executors["claude-cli"] = sp.GetRequiredService<ClaudeAgentExecutor>();

    if (detectedProviders.Contains("codex") && unsafeMode)
        executors["codex"] = sp.GetRequiredService<CodexAgentExecutor>();

    if (detectedProviders.Contains("docker") || detectedProviders.Contains("docker-claude-cli"))
    {
        var dockerExecutor = sp.GetRequiredService<DockerClaudeAgentExecutor>();
        executors["docker-claude-cli"] = dockerExecutor;

        // In docker-claude-cli mode, transparently redirect "claude-cli" roles to Docker
        // so workflow configs don't need modification.
        if (dockerModeRequested)
            executors["claude-cli"] = dockerExecutor;
    }

    if (detectedProviders.Contains("docker") || detectedProviders.Contains("docker-opencode"))
    {
        executors["docker-opencode"] = sp.GetRequiredService<DockerOpenCodeAgentExecutor>();
    }

    if (detectedProviders.Contains("docker") || detectedProviders.Contains("docker-claude-qwen"))
    {
        executors["docker-claude-qwen"] = sp.GetRequiredService<DockerClaudeQwenAgentExecutor>();
    }

    if (detectedProviders.Contains("docker") || detectedProviders.Contains("docker-codex"))
    {
        executors["docker-codex"] = sp.GetRequiredService<DockerCodexAgentExecutor>();
    }

    return new AgentExecutorResolver(executors);
});

// Pre-flight config validation — flags contradictions (e.g. GitHubProjects
// populated but BoardProvider is stub) before tenant resolution would throw
// with a less-informative message. Uses a temporary console logger since the
// host isn't built yet.
{
    using var preHostLoggerFactory = LoggerFactory.Create(b =>
    {
        b.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
    });
    var preHostLogger = preHostLoggerFactory.CreateLogger("Config");
    var findings = StartupConfigValidator.Validate(builder.Configuration);
    if (!StartupConfigValidator.LogAndMaybeExit(findings, preHostLogger))
        Environment.Exit(1);
}

// Tenant identifier: scopes all DB rows and Docker container names to this
// project so multiple projects can share one Postgres instance.
// Resolved eagerly so any missing required config fails fast at startup
// (before the host starts and before any rows could be written untenanted).
{
    var ghOpts = boardProvider == "github"
        ? builder.Configuration.GetSection(GitHubProjectsOptions.SectionName).Get<GitHubProjectsOptions>()
        : null;
    var trelloOpts = boardProvider is "trello" or "live"
        ? builder.Configuration.GetSection(TrelloClientOptions.SectionName).Get<TrelloClientOptions>()
        : null;
    var stubName = builder.Configuration["Stub:TenantName"];

    var tenant = TenantIdentifierFactory.Create(boardProvider, ghOpts, trelloOpts, stubName);
    builder.Services.AddSingleton(tenant);
}

// Generate agent identity for this process instance
var agentIdentity = AgentIdentity.Generate();
builder.Services.AddSingleton(agentIdentity);

// Graceful shutdown coordinator — shared across runners and the Ctrl+C signal handler
// First Ctrl+C sets IsShutdownRequested (finish current work, then exit)
// Second Ctrl+C cancels the main CancellationToken for hard abort
var shutdownCoordinator = new ShutdownCoordinator();
builder.Services.AddSingleton(shutdownCoordinator);

// Docker-Claude agent options (always registered; defaults used when neither section is set).
// Legacy `Docker` section binds first; new `DockerAgents:Claude` section wins on conflict.
builder.Services.Configure<DockerClaudeAgentOptions>(
    builder.Configuration.GetSection(DockerClaudeAgentOptions.LegacySectionName));
builder.Services.Configure<DockerClaudeAgentOptions>(
    builder.Configuration.GetSection(DockerClaudeAgentOptions.SectionName));
builder.Services.AddSingleton(sp => sp.GetRequiredService<IOptions<DockerClaudeAgentOptions>>().Value);

// Mount builder: builds workspace/credential mounts for Docker/Claude container execution
builder.Services.AddSingleton<DockerClaudeMountBuilder>();

// Agent mode services — resolve GitHub token for authenticated image downloads
string? ghImageToken = null;
if (boardProvider == "github")
{
    try
    {
        var ghTokenPsi = new System.Diagnostics.ProcessStartInfo("gh", "auth token")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        ProcessRunner.ConfigureUtf8Io(ghTokenPsi);
        using var ghTokenProc = System.Diagnostics.Process.Start(ghTokenPsi);
        if (ghTokenProc is not null)
        {
            ghImageToken = (await ghTokenProc.StandardOutput.ReadToEndAsync()).Trim();
            await ghTokenProc.WaitForExitAsync();
            if (ghTokenProc.ExitCode != 0) ghImageToken = null;
        }
    }
    catch { /* gh not available — image downloads will be best-effort */ }
}

builder.Services.AddHttpClient("ImageDownloader", client =>
{
    client.DefaultRequestHeaders.UserAgent.ParseAdd("TaskBoard-Worker/1.0");
    if (!string.IsNullOrEmpty(ghImageToken))
        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", ghImageToken);
});
builder.Services.AddSingleton<ImageDownloader>();
builder.Services.AddSingleton<TaskFileManager>();

var worktreeBaseRaw = builder.Configuration["WorktreeBasePath"];
var worktreeBasePath = worktreeBaseRaw is not null ? CliDefinitions.ResolvePath(worktreeBaseRaw) : null;
var gitTimeoutSeconds = int.TryParse(builder.Configuration["GitTimeoutSeconds"], out var gts) ? gts : 30;
builder.Services.AddSingleton(sp =>
    new GitWorkspaceManager(sp.GetRequiredService<ILogger<GitWorkspaceManager>>(), worktreeBasePath, gitTimeoutSeconds));

builder.Services.AddSingleton<UpdateFileProcessor>();
// Rerun redesign Problem 2: per-kind comment routing (append / delete_and_repost / upsert).
// Optional dep on emit sites; direct fallback paths still emit aiboard-log
// markers, while the registered router applies per-kind retention from the
// workflow config's rerun.comments.retentionPolicy block. The Bind(workflowConfig)
// call runs immediately after the host is built (see below).
builder.Services.AddSingleton<ICommentRouter, CommentRouter>();
// Rerun redesign Problem 1: deterministic-skip cache gate. Optional dep on
// AgentRunner — registering it here turns on the cache check for every
// single-agent step on every card. Cache misses still pay the executor cost;
// hits skip the executor and reuse the prior step_result.
builder.Services.AddSingleton<RerunCacheGate>();
builder.Services.Configure<ResourcePoolOptions>(
    builder.Configuration.GetSection(ResourcePoolOptions.SectionName));
builder.Services.AddSingleton<IResourcePool, ResourcePool>();
builder.Services.AddSingleton<DependencyGuard>();
builder.Services.AddSingleton<CandidateExecutor>();
builder.Services.AddSingleton<AgentRunner>();
builder.Services.AddSingleton<MergeRunner>();
builder.Services.AddSingleton<CompletionRunner>();
builder.Services.AddSingleton<PollingRunner>();

// ── Database services (registered if Database connection is configured) ───────
var dbConnectionString = builder.Configuration.GetSection(DatabaseOptions.SectionName)["ConnectionString"];
if (!string.IsNullOrWhiteSpace(dbConnectionString))
{
    builder.Services.AddSingleton(NpgsqlDataSource.Create(dbConnectionString));
    builder.Services.AddSingleton<IRunStore, PgRunStore>();
    builder.Services.AddSingleton<IDependencyWaitStore, PgDependencyWaitStore>();
    builder.Services.AddSingleton<IMetricsStore, PgMetricsStore>();
    builder.Services.AddSingleton<MetricsRunner>();
    builder.Services.Configure<PgmqOptions>(builder.Configuration.GetSection(PgmqOptions.SectionName));
    builder.Services.AddSingleton<IPingQueueClient, PgmqPingQueueClient>();
    builder.Services.AddSingleton<ICardClaimService, CardClaimService>();
    builder.Services.AddSingleton<QueueDrivenRunner>();
}
else
{
    builder.Services.AddSingleton<IRunStore>(NullRunStore.Instance);
    builder.Services.AddSingleton<IDependencyWaitStore>(NullDependencyWaitStore.Instance);
    builder.Services.AddSingleton<IMetricsStore>(NullMetricsStore.Instance);
    builder.Services.AddSingleton<MetricsRunner>();
}

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program");

{
    var resolvedTenant = host.Services.GetRequiredService<ITenantIdentifier>();
    logger.LogInformation("Tenant: {Tenant} (hash={Hash})", resolvedTenant.Value, resolvedTenant.ShortHash);
}

void LogMissingConfig(string requiredKeys)
{
    logger.LogError(
        "Required configuration missing: {Keys}\nConfig sources searched (in precedence order, lowest first):\n{Sources}\n" +
        "Set the value in any of the JSON files above, as an environment variable (use '__' for ':'), or via the matching CLI flag.",
        requiredKeys, string.Join("\n", configSources));
}

// ── 7. Runtime prerequisite validation ───────────────────────────────────────
{
    var rawAgentExec = builder.Configuration["AgentExecutor"];

    // Recognized selection modes — no warning needed
    if (rawAgentExec is not null
        && !string.Equals(rawAgentExec, "stub", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(rawAgentExec, "docker-claude-cli", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(rawAgentExec, "docker-opencode", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(rawAgentExec, "docker-claude-qwen", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(rawAgentExec, "docker-codex", StringComparison.OrdinalIgnoreCase))
    {
        // Warn if AGENT_EXECUTOR is set to a real provider name (now deprecated for provider selection)
        logger.LogWarning(
            "AGENT_EXECUTOR is set to '{Value}' but this value is no longer used for provider selection — " +
            "real providers are now auto-detected. Only 'stub', 'docker-claude-cli', 'docker-codex', 'docker-opencode', and 'docker-claude-qwen' retain special meaning.",
            rawAgentExec);
    }

    // Fail fast when docker-claude-cli is explicitly requested but Docker is unavailable
    if (dockerModeRequested && !detectedProviders.Contains("docker-claude-cli"))
    {
        logger.LogError(
            "AGENT_EXECUTOR=docker-claude-cli is configured but Docker is not available. " +
            "Ensure the Docker CLI is installed and the Docker daemon is running ('docker info' must succeed).");
        return;
    }

    // Fail fast when docker-opencode is explicitly requested but Docker is unavailable
    if (openCodeModeRequested && !detectedProviders.Contains("docker-opencode"))
    {
        logger.LogError(
            "AGENT_EXECUTOR=docker-opencode is configured but Docker is not available. " +
            "Ensure the Docker CLI is installed and the Docker daemon is running ('docker info' must succeed), " +
            "and that the aiboard-opencode-sandbox image has been built (scripts/build-opencode-sandbox.ps1).");
        return;
    }

    // Fail fast when docker-claude-qwen is explicitly requested but Docker is unavailable
    if (claudeQwenModeRequested && !detectedProviders.Contains("docker-claude-qwen"))
    {
        logger.LogError(
            "AGENT_EXECUTOR=docker-claude-qwen is configured but Docker is not available. " +
            "Ensure the Docker CLI is installed and the Docker daemon is running ('docker info' must succeed), " +
            "and that the aiboard-agent-sandbox image (Claude CLI) has been built (scripts/build-sandbox.ps1).");
        return;
    }

    // Fail fast when docker-codex is explicitly requested but Docker is unavailable
    if (codexDockerModeRequested && !detectedProviders.Contains("docker-codex"))
    {
        logger.LogError(
            "AGENT_EXECUTOR=docker-codex is configured but Docker is not available. " +
            "Ensure the Docker CLI is installed and the Docker daemon is running ('docker info' must succeed), " +
            "and that the aiboard-codex-sandbox image has been built (scripts/build-codex-sandbox.ps1).");
        return;
    }

    var config = host.Services.GetRequiredService<WorkflowConfig>();

    // Rerun redesign Problem 2: bind the per-kind retention policy from
    // workflow config into the comment router. Late-binding via setter avoids
    // a circular DI registration (router needs config; config registration is
    // a factory that in turn lazily resolves services). After this call, every
    // emit site that consults the router gets the per-kind override map; sites
    // without an override fall through to CommentRouter.DefaultFor(kind).
    if (host.Services.GetService<ICommentRouter>() is CommentRouter cr)
        cr.Bind(config);

    // ── --unsafe gating ──────────────────────────────────────────────────────
    // Host CLI executors (claude-cli, codex) bypass the Docker filesystem
    // sandbox. The gate is centralized in UnsafeGate.Evaluate so its logic
    // (stub-mode and docker-claude-cli-mode carve-outs) is unit-tested. See
    // UnsafeGateTests for the full scenario coverage.
    {
        var gateResult = UnsafeGate.Evaluate(config, unsafeMode, agentExecutorMode);
        if (!gateResult.Allow)
        {
            logger.LogError("{ErrorMessage}", gateResult.ErrorMessage);
            return;
        }

        if (unsafeMode)
        {
            logger.LogWarning(
                "**UNSAFE MODE ENABLED** — host CLI agents (claude-cli, codex) may run with " +
                "full filesystem and credential access. The container sandbox is bypassed. " +
                "Use only in trusted, isolated environments.");
        }
    }

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

    // CLI version checks against CliVersionPolicy.KnownGood. Old → block
    // startup; Newer → loud warning + proceed; Supported/Unknown → log + proceed.
    // See CliVersionStartupCheck for the severity rules.
    if (detectedProviders.Contains("codex"))
    {
        var codexOpts = host.Services.GetRequiredService<IOptions<CodexCliLlmOptions>>().Value;
        var codexOk = await CliVersionStartupCheck.CheckAsync(
            CliKey.Codex, codexOpts.ExecutablePath, logger);
        if (!codexOk) return;
    }

    // Claude CLI is exercised via three providers (claude-cli direct,
    // docker-claude-cli, docker-claude-qwen). Only check the host-installed
    // version when at least one path uses the host CLI directly. Docker
    // paths bake the CLI into the sandbox image — that version is checked
    // against the same policy from inside the container at first use, but
    // not from here.
    if (detectedProviders.Contains("claude-cli") && !dockerModeRequested)
    {
        var claudeOpts = host.Services.GetRequiredService<IOptions<ClaudeCliLlmOptions>>().Value;
        var claudeOk = await CliVersionStartupCheck.CheckAsync(
            CliKey.Claude, claudeOpts.ExecutablePath, logger);
        if (!claudeOk) return;
    }

    // Check for orphaned aiboard-* containers from prior crashed runs
    var orphanedContainers = await PrerequisiteValidator.DetectOrphanedContainersAsync();
    if (orphanedContainers.Count > 0)
    {
        logger.LogWarning(
            "Found {Count} orphaned aiboard container(s) from prior runs: {Names}. " +
            "These may consume resources. Run 'docker rm -f {JoinedNames}' to clean up.",
            orphanedContainers.Count,
            string.Join(", ", orphanedContainers),
            string.Join(" ", orphanedContainers));
    }

    if (!string.IsNullOrWhiteSpace(dbConnectionString))
    {
        var (pgOk, pgError) = await PrerequisiteValidator.ValidatePostgresAsync(
            dbConnectionString);
        if (!pgOk)
        {
            logger.LogError("PostgreSQL connection failed: {Error}", pgError);
            return;
        }
        logger.LogInformation("PostgreSQL connection verified");
    }
    else
    {
        logger.LogWarning(
            "Database:ConnectionString not configured — run tracking and metrics disabled (using NullRunStore)");
    }
}

logger.LogInformation("Agent identity: {AgentName}", agentIdentity.DisplayName);

// ── Diagnostic: dump resolved configuration values ──────────────────────────
logger.LogInformation("Content root: {ContentRoot}", builder.Environment.ContentRootPath);
logger.LogInformation("AppContext.BaseDirectory: {BaseDir}", AppContext.BaseDirectory);
logger.LogInformation("Raw config ClaudeCli:TimeoutSeconds = {RawTimeout}",
    builder.Configuration["ClaudeCli:TimeoutSeconds"] ?? "(not set)");
if (detectedProviders.Contains("claude-cli") && agentExecutorMode != "stub" && !dockerModeRequested)
{
    var claudeOpts = host.Services.GetRequiredService<IOptions<ClaudeCliLlmOptions>>().Value;
    logger.LogInformation(
        "ClaudeCliLlmOptions: ExecutablePath={Exe}, TimeoutSeconds={Timeout}, MaxBudgetUsd={Budget}, MaxTurns={Turns}",
        claudeOpts.ExecutablePath, claudeOpts.TimeoutSeconds, claudeOpts.MaxBudgetUsd, claudeOpts.MaxTurns);
}
if (dockerModeRequested && detectedProviders.Contains("docker-claude-cli"))
{
    var dockerOpts = host.Services.GetRequiredService<IOptions<DockerClaudeAgentOptions>>().Value;
    logger.LogInformation(
        "DockerClaudeAgentOptions: ImageName={Image}, NetworkMode={Network}, MemoryLimit={Memory}, CredentialPath={Creds}",
        dockerOpts.ImageName, dockerOpts.NetworkMode, dockerOpts.MemoryLimit ?? "(none)", dockerOpts.CredentialPath);
}
if (detectedProviders.Contains("docker-opencode"))
{
    var openCodeOpts = host.Services.GetRequiredService<IOptions<DockerOpenCodeAgentOptions>>().Value;
    logger.LogInformation(
        "DockerOpenCodeAgentOptions: ImageName={Image}, NetworkMode={Network}, ProviderBaseUrl={Url}, ModelName={Model}",
        openCodeOpts.ImageName, openCodeOpts.NetworkMode,
        openCodeOpts.ProviderBaseUrl, openCodeOpts.ModelName);
    logger.LogInformation(
        "OpenCode executor registered. Ensure the 'llm-net' Docker network exists " +
        "(start the local-llm compose project) before routing roles to 'docker-opencode'.");
}
if (detectedProviders.Contains("docker-claude-qwen"))
{
    var cqOpts = host.Services.GetRequiredService<IOptions<DockerClaudeQwenAgentOptions>>().Value;
    logger.LogInformation(
        "DockerClaudeQwenAgentOptions: ImageName={Image}, NetworkMode={Network}, ProviderBaseUrl={Url}, ModelName={Model}, AttributionHeader={Attr}, NonessentialTraffic={Ness}",
        cqOpts.ImageName, cqOpts.NetworkMode,
        cqOpts.ProviderBaseUrl, cqOpts.ModelName,
        cqOpts.DisableAttributionHeader ? "off" : "on",
        cqOpts.DisableNonessentialTraffic ? "off" : "on");
    logger.LogInformation(
        "Claude→Qwen executor registered. Ensure the 'llm-net' Docker network exists " +
        "(start the local-llm compose project) before routing roles to 'docker-claude-qwen'.");
}

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
        LogMissingConfig("CardId (or --card-id) — required for agent mode");
        return;
    }

    if (string.IsNullOrWhiteSpace(boardId))
    {
        LogMissingConfig("BoardId / GitHubProjects:ProjectNumber (or --board-id) — required for agent mode");
        return;
    }

    if (string.IsNullOrWhiteSpace(workspacePath))
    {
        LogMissingConfig("AgentWorkspacePath (or --workspace) — required for agent mode");
        return;
    }

    using var scope = host.Services.CreateScope();

    logger.LogInformation("Agent mode: card={CardId} board={BoardId} workspace={Workspace}",
        cardId, boardId, workspacePath);

    // Determine dispatch: fetch card to check if it's in a system_merge state
    var boardClientInstance = scope.ServiceProvider.GetRequiredService<ITaskBoardClient>();
    var workflowConfigInstance = scope.ServiceProvider.GetRequiredService<WorkflowConfig>();
    var boardCards = await boardClientInstance.GetBoardCardsAsync(boardId, CancellationToken.None, workflowConfigInstance.GetPollingExcludedColumnNames());
    var targetCard = boardCards.FirstOrDefault(c => c.Id == cardId);

    // Allow --state override for manual dispatch when filter-based resolution is insufficient
    var stateOverride = builder.Configuration["StateOverride"];

    AgentRunResult result;
    var cardState = stateOverride is not null
        ? workflowConfigInstance.States.GetValueOrDefault(stateOverride)
        : (targetCard is not null ? workflowConfigInstance.ResolveState(targetCard) : null);
    try
    {
        if (cardState is not null
            && string.Equals(cardState.GateType, GateTypes.SystemMerge, StringComparison.OrdinalIgnoreCase))
        {
            var mergeRunner = scope.ServiceProvider.GetRequiredService<MergeRunner>();
            result = await mergeRunner.ExecuteAsync(cardId, boardId, workspacePath, CancellationToken.None);
        }
        else if (cardState is not null
            && string.Equals(cardState.GateType, GateTypes.ChildrenComplete, StringComparison.OrdinalIgnoreCase))
        {
            var completionRunnerInstance = scope.ServiceProvider.GetRequiredService<CompletionRunner>();
            result = await completionRunnerInstance.ExecuteAsync(cardId, boardId, workspacePath, CancellationToken.None);
        }
        else
        {
            var agentRunner = scope.ServiceProvider.GetRequiredService<AgentRunner>();
            result = await agentRunner.ExecuteAsync(cardId, boardId, workspacePath, CancellationToken.None);
        }
    }
    catch (RateLimitException rateLimitEx)
    {
        logger.LogWarning(rateLimitEx,
            "Agent rate limited for card {CardId}. Card has been restored to its trigger column. " +
            "Retry when the rate-limit window resets.",
            cardId);
        return;
    }

    logger.LogInformation("Agent run complete: outcome={Outcome}, error={ErrorDetail}",
        result.Outcome, result.ErrorDetail ?? "(none)");
    return;
}

if (mode == "polling")
{
    if (string.IsNullOrWhiteSpace(boardId))
    {
        LogMissingConfig("BoardId / GitHubProjects:ProjectNumber (or --board-id) — required for polling mode");
        return;
    }

    if (string.IsNullOrWhiteSpace(workspacePath))
    {
        LogMissingConfig("AgentWorkspacePath (or --workspace) — required for polling mode");
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
        if (shutdownCoordinator.RequestShutdown())
        {
            // First Ctrl+C — graceful shutdown
            logger.LogWarning(
                "Shutdown requested — finishing current work before exiting. " +
                "Press Ctrl+C again to force quit immediately.");
        }
        else
        {
            // Second Ctrl+C — hard abort
            logger.LogWarning("Force shutdown initiated.");
            cts.Cancel();
        }
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
        LogMissingConfig("BoardId (or --board-id) — required for queue mode");
        return;
    }

    if (string.IsNullOrWhiteSpace(workspacePath))
    {
        LogMissingConfig("AgentWorkspacePath (or --workspace) — required for queue mode");
        return;
    }

    if (string.IsNullOrWhiteSpace(dbConnectionString))
    {
        LogMissingConfig("Database:ConnectionString (or --db-connection) — required for queue mode");
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
        if (shutdownCoordinator.RequestShutdown())
        {
            // First Ctrl+C — graceful shutdown
            logger.LogWarning(
                "Shutdown requested — finishing current work before exiting. " +
                "Press Ctrl+C again to force quit immediately.");
        }
        else
        {
            // Second Ctrl+C — hard abort
            logger.LogWarning("Force shutdown initiated.");
            cts.Cancel();
        }
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

if (mode == "validation")
{
    if (string.IsNullOrWhiteSpace(boardId))
    {
        LogMissingConfig("BoardId / GitHubProjects:ProjectNumber (or --board-id) — required for validation mode");
        return;
    }

    using var scope = host.Services.CreateScope();
    var workflowCfg = scope.ServiceProvider.GetRequiredService<WorkflowConfig>();
    var probe = scope.ServiceProvider.GetRequiredService<IBoardShapeProbe>();
    var dockerImageProbe = scope.ServiceProvider.GetRequiredService<IDockerImageProbe>();
    var runnerLogger = scope.ServiceProvider.GetRequiredService<ILogger<ValidationRunner>>();
    var validationRunner = new ValidationRunner(
        workflowCfg, probe, dockerImageProbe, boardProvider, boardId, runnerLogger);

    var exitCode = await validationRunner.RunAsync(CancellationToken.None);
    Environment.ExitCode = exitCode;
    return;
}

if (mode == "diagnose")
{
    var cardId = builder.Configuration["CardId"];
    if (string.IsNullOrWhiteSpace(cardId))
    {
        LogMissingConfig("CardId (or --card-id) — required for diagnose mode");
        return;
    }

    using var scope = host.Services.CreateScope();
    var boardClient = scope.ServiceProvider.GetRequiredService<ITaskBoardClient>();
    var workflowCfg = scope.ServiceProvider.GetRequiredService<WorkflowConfig>();
    var diagnoseLogger = scope.ServiceProvider.GetRequiredService<ILogger<DiagnoseRunner>>();
    // DependencyGuard is registered unconditionally (with a Null waitStore when
    // no DB connection is configured), so we always pass it. CheckAsync is a
    // no-op when DependencyPolicy.Enabled is false.
    var depGuard = scope.ServiceProvider.GetService<DependencyGuard>();
    var diagnoseRunner = new DiagnoseRunner(boardClient, workflowCfg, diagnoseLogger,
        dependencyGuard: depGuard);

    var exitCode = await diagnoseRunner.RunAsync(cardId, CancellationToken.None);
    Environment.ExitCode = exitCode;
    return;
}

if (mode == "scaffold-board")
{
    if (string.IsNullOrWhiteSpace(boardId))
    {
        LogMissingConfig("BoardId / GitHubProjects:ProjectNumber (or --board-id) — required for scaffold-board mode");
        return;
    }

    var apply = string.Equals(builder.Configuration["Scaffold:Apply"], "true",
        StringComparison.OrdinalIgnoreCase);

    using var scope = host.Services.CreateScope();
    var workflowCfg = scope.ServiceProvider.GetRequiredService<WorkflowConfig>();
    var probe = scope.ServiceProvider.GetRequiredService<IBoardShapeProbe>();
    var applier = scope.ServiceProvider.GetRequiredService<IBoardShapeApplier>();
    var scaffoldLogger = scope.ServiceProvider.GetRequiredService<ILogger<ScaffoldBoardRunner>>();
    var scaffoldRunner = new ScaffoldBoardRunner(workflowCfg, probe, applier, scaffoldLogger);

    var exitCode = await scaffoldRunner.RunAsync(boardId, apply, CancellationToken.None);
    Environment.ExitCode = exitCode;
    return;
}

if (mode == "metrics")
{
    var cardId = builder.Configuration["CardId"];
    var sinceArg = builder.Configuration["Since"];
    var since = SinceParser.Parse(sinceArg);

    if (sinceArg is not null && since is null)
    {
        logger.LogError(
            "Invalid --since value '{SinceArg}'. Use format <N><unit> where unit is h, d, or w. Example: 7d",
            sinceArg);
        return;
    }

    using var scope = host.Services.CreateScope();
    var metricsRunner = scope.ServiceProvider.GetRequiredService<MetricsRunner>();
    await metricsRunner.RunAsync(cardId, since, CancellationToken.None);
    return;
}

// No recognized mode — show diagnostic + help
LogMissingConfig(
    mode is null
        ? "Mode (or --mode) — must be agent, polling, queue, metrics, or validation"
        : $"Mode='{mode}' is not recognized — must be agent, polling, queue, metrics, or validation");
CliDefinitions.PrintHelp();

// ── Helpers ──────────────────────────────────────────────────────────────────

static string? PreParseArg(string[] args, string key)
{
    var i = Array.FindIndex(args, a => string.Equals(a, key, StringComparison.OrdinalIgnoreCase));
    return (i >= 0 && i + 1 < args.Length) ? args[i + 1] : null;
}
