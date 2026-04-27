namespace TaskBoard.Worker;

/// <summary>
/// CLI flag definitions, switch mappings for IConfiguration, and help output.
/// </summary>
internal static class CliDefinitions
{
    /// <summary>
    /// Maps --kebab-case CLI flags to IConfiguration keys.
    /// Used by <see cref="Microsoft.Extensions.Configuration.CommandLineConfigurationExtensions.AddCommandLine"/>.
    /// </summary>
    public static readonly Dictionary<string, string> SwitchMappings = new(StringComparer.OrdinalIgnoreCase)
    {
        // CLI-only (not in appsettings, but routed through IConfiguration for uniform access)
        ["--mode"] = "Mode",
        ["--card-id"] = "CardId",
        ["--state"] = "StateOverride",
        ["--config"] = "ConfigPath",
        ["--prompt-root"] = "PromptRoot",

        // General
        ["--board-provider"] = "BoardProvider",
        ["--agent-executor"] = "AgentExecutor",
        ["--workflow-config"] = "WorkflowConfigPath",
        ["--board-id"] = "BoardId",
        ["--workspace"] = "AgentWorkspacePath",
        ["--worktree-base"] = "WorktreeBasePath",
        ["--poll-interval"] = "PollIntervalSeconds",

        // GitHub Projects
        ["--github-owner"] = "GitHubProjects:Owner",
        ["--github-repo"] = "GitHubProjects:Repo",
        ["--github-project"] = "GitHubProjects:ProjectNumber",
        ["--github-status-field"] = "GitHubProjects:StatusFieldName",
        ["--github-agent-username"] = "GitHubProjects:AgentUsername",

        // Claude CLI
        ["--claude-path"] = "ClaudeCli:ExecutablePath",
        ["--claude-max-turns"] = "ClaudeCli:MaxTurns",
        ["--claude-max-budget"] = "ClaudeCli:MaxBudgetUsd",
        ["--claude-timeout"] = "ClaudeCli:TimeoutSeconds",

        // Trello
        ["--trello-api-key"] = "Trello:ApiKey",
        ["--trello-api-token"] = "Trello:ApiToken",
        ["--trello-base-url"] = "Trello:BaseUrl",
        ["--trello-agent-username"] = "Trello:AgentUsername",

        // Database
        ["--db-connection"] = "Database:ConnectionString",
        ["--neon-connection"] = "Database:ConnectionString",   // alias for backward compat

        // PGMQ / Queue mode
        ["--ping-queue"] = "Pgmq:PingQueueName",
        ["--stale-claim-minutes"] = "Pgmq:StaleClaimMinutes",
        ["--max-concurrent-agents"] = "Pgmq:MaxConcurrentAgents",

        // Metrics mode
        ["--since"] = "Since",
    };

    /// <summary>
    /// Resolves a path value using the standard convention:
    ///   - Starts with '.' → relative to current working directory
    ///   - Absolute path   → used as-is (normalized)
    ///   - Otherwise       → relative to the executable directory
    /// </summary>
    public static string ResolvePath(string path)
    {
        if (path.StartsWith('.'))
            return Path.GetFullPath(path);

        if (Path.IsPathRooted(path))
            return Path.GetFullPath(path);

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));
    }

    public static bool ShouldShowHelp(string[] args) =>
        Array.Exists(args, a => a is "--help" or "-h" or "-?");

    /// <summary>
    /// Common typo / wrong-form flags mapped to a hint about the canonical form.
    /// Hits print a "did you mean" line in addition to the generic unknown-flag
    /// rejection. Add new entries when the same shape gets typed often enough.
    /// </summary>
    private static readonly Dictionary<string, string> CommonTypoHints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["--validate"]   = "--mode validation",
        ["--validation"] = "--mode validation",
        ["--metrics"]    = "--mode metrics",
        ["--polling"]    = "--mode polling",
        ["--agent"]      = "--mode agent",
        ["--queue"]      = "--mode queue",
        ["--help-me"]    = "--help",
    };

    /// <summary>
    /// Result of a CLI-arg sanity check. <see cref="UnknownFlags"/> is empty
    /// when every <c>--foo</c>/<c>-x</c> token in <paramref name="args"/> is
    /// either a recognised switch in <see cref="SwitchMappings"/>, the help
    /// flag, or a value attached to one (e.g. <c>--mode=agent</c>'s <c>=agent</c>
    /// half is consumed by the previous flag).
    /// </summary>
    public sealed record UnknownFlagsReport(
        IReadOnlyList<string> UnknownFlags,
        IReadOnlyDictionary<string, string> TypoHints);

    /// <summary>
    /// Scans <paramref name="args"/> for tokens that look like flags
    /// (<c>--name</c> or <c>-x</c>) but aren't in <see cref="SwitchMappings"/>
    /// or the help-flag set. AddCommandLine silently ignores unknown flags,
    /// which means a typo like <c>--validate</c> falls through to whatever
    /// <c>Mode</c> is set in appsettings.user.json (e.g. polling mode), which
    /// is exactly the v0.0.15 surprise KvA hit. Call this immediately after
    /// AddCommandLine and exit early on a non-empty report.
    /// </summary>
    public static UnknownFlagsReport ValidateKnownFlags(string[] args)
    {
        var unknown = new List<string>();
        var hints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (string.IsNullOrEmpty(arg)) continue;
            if (!arg.StartsWith('-')) continue;

            // `--name=value` form: only the `--name` half needs to match.
            var flagToken = arg;
            var eqIdx = arg.IndexOf('=');
            if (eqIdx > 0) flagToken = arg[..eqIdx];

            if (flagToken is "--help" or "-h" or "-?") continue;
            if (SwitchMappings.ContainsKey(flagToken)) continue;

            unknown.Add(flagToken);
            if (CommonTypoHints.TryGetValue(flagToken, out var hint))
                hints[flagToken] = hint;
        }

        return new UnknownFlagsReport(unknown, hints);
    }

    public static void PrintHelp()
    {
        const int pad = 36;

        Console.WriteLine("aiboard - AI Kanban Agent Orchestrator");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  aiboard --mode agent --card-id 3 --board-id 1 --workspace .");
        Console.WriteLine("  aiboard --mode polling --board-id 1 --workspace .");
        Console.WriteLine("  aiboard --mode metrics [--card-id 3] [--since 7d]");
        Console.WriteLine("  aiboard --mode validation --board-id 1     (read-only check of workflow vs. board)");
        Console.WriteLine("  aiboard --config project-a.json --mode polling");
        Console.WriteLine("  aiboard                               (uses ./.aiboard/appsettings.json + ./.aiboard/workflow.json)");
        Console.WriteLine();
        Console.WriteLine("All options can also be set via appsettings.json, appsettings.user.json,");
        Console.WriteLine("a --config file, or environment variables (e.g. GitHubProjects__Repo).");
        Console.WriteLine("Precedence (lowest -> highest):");
        Console.WriteLine("  exe-dir < cwd/.aiboard < cwd < --config < env vars < CLI args");
        Console.WriteLine();
        Console.WriteLine("Path resolution for --config, --prompt-root, --workflow-config,");
        Console.WriteLine("--workspace, --worktree-base:");
        Console.WriteLine("  ./relative or ../up    relative to current working directory");
        Console.WriteLine("  C:\\absolute or /abs    used as-is");
        Console.WriteLine("  bare/path              relative to the aiboard executable directory");
        Console.WriteLine();

        Console.WriteLine("Shutdown behavior (polling and queue modes):");
        Console.WriteLine("  Ctrl+C (first)   Graceful shutdown — finishes the current card, then exits.");
        Console.WriteLine("  Ctrl+C (second)  Force quit — cancels the active operation immediately.");
        Console.WriteLine();

        WriteSection("General", [
            ("--mode <mode>",             "Execution mode: agent, polling, metrics, validation", null),
            ("--card-id <id>",            "Card/issue number (required for agent mode)",       null),
            ("--state <stateId>",         "Override state key for agent mode dispatch (bypasses filter-based resolution)", null),
            ("--config <path>",           "Additional JSON config file to layer in",           null),
            ("--prompt-root <path>",      "Base directory for prompt file resolution",         "exe directory"),
            ("--board-provider <name>",   "Board provider: stub, trello, github",              "stub"),
            ("--agent-executor <name>",   "Agent executor: stub, claude-cli",                  "stub"),
            ("--workflow-config <path>",  "Path to workflow JSON file",                        "workflow.v1.json (exe dir)"),
            ("--board-id <id>",           "Board identifier / project number",                 null),
            ("--workspace <path>",        "Agent workspace / repository path",                 null),
            ("--worktree-base <path>",    "Base path for git worktrees",                       null),
            ("--poll-interval <secs>",    "Base polling interval in seconds (adaptive backoff scales up on idle/error)", "120"),
        ], pad);

        WriteSection("GitHub Projects", [
            ("--github-owner <owner>",          "GitHub org or user that owns the project",    null),
            ("--github-repo <owner/repo>",      "Repository in owner/repo format",             null),
            ("--github-project <number>",       "GitHub project number",                       null),
            ("--github-status-field <name>",    "Status field name in the project",            "Status"),
            ("--github-agent-username <name>",  "Override agent's GitHub username",             null),
        ], pad);

        WriteSection("Claude CLI", [
            ("--claude-path <path>",        "Path to Claude CLI executable",        "claude"),
            ("--claude-max-turns <n>",      "Max agent turns per invocation",       "5"),
            ("--claude-max-budget <usd>",   "Max budget in USD per invocation",     "10.00"),
            ("--claude-timeout <secs>",     "Claude CLI timeout in seconds",        "900"),
        ], pad);

        WriteSection("Trello", [
            ("--trello-api-key <key>",          "Trello API key",                   null),
            ("--trello-api-token <token>",      "Trello API token",                 null),
            ("--trello-base-url <url>",         "Trello API base URL",              "https://api.trello.com"),
            ("--trello-agent-username <name>",  "Override agent's Trello username",  null),
        ], pad);

        WriteSection("Database / Metrics", [
            ("--db-connection <conn>",  "PostgreSQL connection string",                         null),
            ("--since <duration>",      "Time window for metrics (e.g. 24h, 7d, 2w)",          "all time"),
        ], pad);
    }

    private static void WriteSection(
        string title, (string flag, string description, string? defaultValue)[] options, int pad)
    {
        Console.WriteLine($"  {title}:");
        foreach (var (flag, description, defaultValue) in options)
        {
            var left = $"    {flag}";
            var desc = defaultValue is not null
                ? $"{description} [default: {defaultValue}]"
                : description;
            Console.WriteLine(left.PadRight(pad) + desc);
        }

        Console.WriteLine();
    }
}
