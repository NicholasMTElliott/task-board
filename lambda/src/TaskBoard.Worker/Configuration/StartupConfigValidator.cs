using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Configuration;

/// <summary>
/// Pre-flight validation of merged configuration. Flags internally inconsistent
/// setups (e.g. GitHubProjects populated but BoardProvider is stub) that would
/// otherwise fail later with confusing "missing required value" errors.
/// </summary>
public static class StartupConfigValidator
{
    public enum Severity { Warning, Error }

    public readonly record struct Finding(Severity Severity, string Key, string Message);

    private static readonly string[] KnownBoardProviders = { "github", "trello", "live", "stub" };
    /// <summary>
    /// Recognised provider keys for <c>AgentExecutor</c> selection, role
    /// providers, candidate overrides, and role fallbacks. Internal so other
    /// validators (notably <see cref="Models.WorkflowConfigValidator"/>) can
    /// cross-check role/fallback provider names against the same source of
    /// truth without duplicating the list.
    /// </summary>
    internal static readonly string[] KnownAgentExecutors = { "stub", "claude-cli", "docker-claude-cli", "docker-codex", "docker-opencode", "docker-claude-qwen", "codex" };

    /// <summary>
    /// Runs all pre-flight checks against the merged configuration and returns
    /// findings. Does not throw — callers decide how to react.
    /// </summary>
    public static IReadOnlyList<Finding> Validate(IConfiguration config)
    {
        var findings = new List<Finding>();

        var boardProvider = (config["BoardProvider"] ?? "").Trim().ToLowerInvariant();
        if (boardProvider.Length == 0) boardProvider = "stub";

        var githubPresent = SectionHasValues(config.GetSection(GitHubProjectsOptions.SectionName));
        var trelloPresent = SectionHasValues(config.GetSection(TrelloClientOptions.SectionName));

        // ── Error 1: unknown BoardProvider ───────────────────────────────────
        var rawProvider = config["BoardProvider"];
        if (!string.IsNullOrWhiteSpace(rawProvider) &&
            !KnownBoardProviders.Contains(boardProvider))
        {
            findings.Add(new Finding(Severity.Error, "BoardProvider",
                $"BoardProvider='{rawProvider}' is not recognized. " +
                $"Accepted values: {string.Join(", ", KnownBoardProviders)}."));
        }

        // ── Error 2: BoardProvider=github but GitHubProjects incomplete ──────
        if (boardProvider == "github")
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(config["GitHubProjects:Owner"])) missing.Add("GitHubProjects:Owner");
            if (string.IsNullOrWhiteSpace(config["GitHubProjects:Repo"])) missing.Add("GitHubProjects:Repo");
            if (string.IsNullOrWhiteSpace(config["GitHubProjects:ProjectNumber"])) missing.Add("GitHubProjects:ProjectNumber");

            if (missing.Count > 0)
            {
                findings.Add(new Finding(Severity.Error, "GitHubProjects",
                    $"BoardProvider=github but required GitHubProjects fields are missing: " +
                    $"{string.Join(", ", missing)}."));
            }
        }

        // ── Error 3: BoardProvider=trello but Trello incomplete ──────────────
        if (boardProvider is "trello" or "live")
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(config["Trello:ApiKey"])) missing.Add("Trello:ApiKey");
            if (string.IsNullOrWhiteSpace(config["Trello:ApiToken"])) missing.Add("Trello:ApiToken");
            if (string.IsNullOrWhiteSpace(config["Trello:BoardId"])) missing.Add("Trello:BoardId");

            if (missing.Count > 0)
            {
                findings.Add(new Finding(Severity.Error, "Trello",
                    $"BoardProvider={boardProvider} but required Trello fields are missing: " +
                    $"{string.Join(", ", missing)}."));
            }
        }

        // ── Error 5: GitHubProjects populated but BoardProvider != github ────
        // Silently dead config is worse than a missing field — promote to error
        // so operators must explicitly resolve the contradiction.
        if (githubPresent && boardProvider != "github")
        {
            findings.Add(new Finding(Severity.Error, "GitHubProjects",
                $"GitHubProjects config is present but BoardProvider='{boardProvider}'. " +
                "These fields would be ignored at runtime. " +
                "Set BoardProvider=github to use them, or remove the GitHubProjects section."));
        }

        // ── Error 6: Trello populated but BoardProvider != trello ────────────
        if (trelloPresent && boardProvider is not ("trello" or "live"))
        {
            findings.Add(new Finding(Severity.Error, "Trello",
                $"Trello config is present but BoardProvider='{boardProvider}'. " +
                "These fields would be ignored at runtime. " +
                "Set BoardProvider=trello to use them, or remove the Trello section."));
        }

        // ── Error 7: both GitHubProjects and Trello populated ────────────────
        // Two board providers in one config is always a mistake — one is silently
        // dead weight. Force operator to delete the unused section.
        if (githubPresent && trelloPresent)
        {
            findings.Add(new Finding(Severity.Error, "BoardProvider",
                "Both GitHubProjects and Trello config sections are populated. " +
                $"BoardProvider='{boardProvider}' — only one of these sections can be active at runtime. " +
                "Remove the section that does not match BoardProvider."));
        }

        // ── Error 8: unknown AgentExecutor value ─────────────────────────────
        // Silently falling back to auto-detect on a typo is the exact misbehavior
        // pattern that hides config bugs.
        var rawExecutor = config["AgentExecutor"];
        if (!string.IsNullOrWhiteSpace(rawExecutor) &&
            !KnownAgentExecutors.Contains(rawExecutor.Trim().ToLowerInvariant()))
        {
            findings.Add(new Finding(Severity.Error, "AgentExecutor",
                $"AgentExecutor='{rawExecutor}' is not recognized. " +
                $"Accepted values: {string.Join(", ", KnownAgentExecutors)}."));
        }

        // ── Error 9: legacy `Docker` section is deprecated ───────────────────
        // The legacy `Docker` section only binds to DockerClaudeAgentOptions —
        // it does NOT affect docker-opencode or docker-claude-qwen. Operators
        // who set Docker:ImageName expecting it to apply to all Docker executors
        // get a silently-partial result. Force migration so each executor's image
        // is set explicitly in its own section.
        var legacyDockerPresent = SectionHasValues(config.GetSection(DockerClaudeAgentOptions.LegacySectionName));
        if (legacyDockerPresent)
        {
            findings.Add(new Finding(Severity.Error, DockerClaudeAgentOptions.LegacySectionName,
                $"Config section '{DockerClaudeAgentOptions.LegacySectionName}' is populated. " +
                $"This section is deprecated — migrate to '{DockerClaudeAgentOptions.SectionName}'. " +
                "Note: the legacy section ONLY affected docker-claude-cli; if you also use " +
                "docker-opencode or docker-claude-qwen, set their image / timeout / etc. " +
                "explicitly under DockerAgents:OpenCode and DockerAgents:ClaudeQwen respectively."));
        }

        // ── Error 10: CodexCli:MaxBudgetUsd is not supported ─────────────────
        // Codex CLI has no budget-cap flag; the value is silently ignored.
        if (!string.IsNullOrWhiteSpace(config["CodexCli:MaxBudgetUsd"]))
        {
            findings.Add(new Finding(Severity.Error, "CodexCli:MaxBudgetUsd",
                "CodexCli:MaxBudgetUsd is set but Codex CLI has no budget-cap flag; " +
                "the value would be silently ignored. " +
                "Use CodexCli:TimeoutSeconds to bound run cost by wall-clock time, " +
                "or remove the setting."));
        }

        // ── Error 11: Docker agents must not run as root ────────────────────
        // Claude CLI rejects bypass-permissions mode when run as root/sudo, and
        // root also changes HOME for several CLIs. Use --group-add for Docker
        // socket permissions instead of changing the runtime user.
        AddRootContainerUserFindings(findings, config, DockerClaudeAgentOptions.SectionName);
        AddRootContainerUserFindings(findings, config, DockerClaudeQwenAgentOptions.SectionName);
        AddRootContainerUserFindings(findings, config, DockerCodexAgentOptions.SectionName);
        AddRootContainerUserFindings(findings, config, DockerOpenCodeAgentOptions.SectionName);

        return findings;
    }

    /// <summary>
    /// Emits warnings (if any) and errors (if any). Returns <c>false</c> when
    /// any <see cref="Severity.Error"/> finding is present, so the caller can
    /// abort startup.
    /// </summary>
    public static bool LogAndMaybeExit(IReadOnlyList<Finding> findings, ILogger logger)
    {
        foreach (var f in findings.Where(x => x.Severity == Severity.Warning))
            logger.LogWarning("Config warning [{Key}]: {Message}", f.Key, f.Message);

        var errors = findings.Where(x => x.Severity == Severity.Error).ToList();
        foreach (var f in errors)
            logger.LogError("Config error [{Key}]: {Message}", f.Key, f.Message);

        return errors.Count == 0;
    }

    private static bool SectionHasValues(IConfigurationSection section)
    {
        // "Populated" = at least one child with a non-whitespace value.
        foreach (var child in section.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value)) return true;
            // Nested section with content also counts.
            if (SectionHasValues(child)) return true;
        }
        return false;
    }

    private static void AddRootContainerUserFindings(
        List<Finding> findings, IConfiguration config, string sectionName)
    {
        var key = $"{sectionName}:ContainerUser";
        var value = config[key]?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return;

        if (!IsRootUser(value)) return;

        findings.Add(new Finding(Severity.Error, key,
            $"{key} is set to '{value}', which runs the agent CLI as root. " +
            "Claude CLI rejects bypass-permissions mode under root/sudo, and other CLIs may drift to /root for credentials. " +
            "Leave ContainerUser unset and use MountHostDockerSocket=true plus GroupAdd=[\"<docker-socket-gid>\"] " +
            "when the non-root agent user needs Docker socket access."));
    }

    private static bool IsRootUser(string value)
    {
        if (string.Equals(value, "root", StringComparison.OrdinalIgnoreCase)) return true;

        var userPart = value.Split(':', 2)[0].Trim();
        return userPart == "0";
    }
}
