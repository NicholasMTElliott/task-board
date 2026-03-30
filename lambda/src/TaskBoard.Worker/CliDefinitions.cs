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
    };

    public static bool ShouldShowHelp(string[] args) =>
        args.Length == 0 || Array.Exists(args, a => a is "--help" or "-h" or "-?");

    public static void PrintHelp()
    {
        const int pad = 36;

        Console.WriteLine("aiboard - AI Kanban Agent Orchestrator");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  aiboard --mode agent --card-id 3 --board-id 1 --workspace .");
        Console.WriteLine("  aiboard --mode polling --board-id 1 --workspace .");
        Console.WriteLine("  aiboard --config project-a.json --mode polling");
        Console.WriteLine();
        Console.WriteLine("All options can also be set via appsettings.json, appsettings.user.json,");
        Console.WriteLine("a --config file, or environment variables (e.g. GitHubProjects__Repo).");
        Console.WriteLine("Precedence: appsettings < --config < appsettings.user < env vars < CLI args.");
        Console.WriteLine();

        WriteSection("General", [
            ("--mode <mode>",             "Execution mode: agent, polling",                    null),
            ("--card-id <id>",            "Card/issue number (required for agent mode)",       null),
            ("--config <path>",           "Additional JSON config file to layer in",           null),
            ("--prompt-root <path>",      "Base directory for prompt file resolution",         "exe directory"),
            ("--board-provider <name>",   "Board provider: stub, trello, github",              "stub"),
            ("--agent-executor <name>",   "Agent executor: stub, claude-cli",                  "stub"),
            ("--workflow-config <path>",  "Path to workflow JSON file",                        "workflow.v1.json (exe dir)"),
            ("--board-id <id>",           "Board identifier / project number",                 null),
            ("--workspace <path>",        "Agent workspace / repository path",                 null),
            ("--worktree-base <path>",    "Base path for git worktrees",                       null),
            ("--poll-interval <secs>",    "Polling interval in seconds",                       "60"),
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
