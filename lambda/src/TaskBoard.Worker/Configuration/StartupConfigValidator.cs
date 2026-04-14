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
    private static readonly string[] KnownAgentExecutors = { "stub", "claude-cli", "docker-claude-cli", "codex" };

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

        // ── Warning 5: GitHubProjects populated but BoardProvider != github ──
        if (githubPresent && boardProvider != "github")
        {
            findings.Add(new Finding(Severity.Warning, "GitHubProjects",
                $"GitHubProjects config is present but BoardProvider='{boardProvider}'. " +
                "These fields will be ignored. Set BoardProvider=github to use them."));
        }

        // ── Warning 6: Trello populated but BoardProvider != trello ──────────
        if (trelloPresent && boardProvider is not ("trello" or "live"))
        {
            findings.Add(new Finding(Severity.Warning, "Trello",
                $"Trello config is present but BoardProvider='{boardProvider}'. " +
                "These fields will be ignored. Set BoardProvider=trello to use them."));
        }

        // ── Warning 7: both GitHubProjects and Trello populated ──────────────
        if (githubPresent && trelloPresent)
        {
            var winner = boardProvider is "trello" or "live" ? "Trello" : "GitHubProjects";
            var loser = winner == "Trello" ? "GitHubProjects" : "Trello";
            findings.Add(new Finding(Severity.Warning, "BoardProvider",
                $"Both GitHubProjects and Trello config sections are populated. " +
                $"BoardProvider='{boardProvider}' — {winner} will be used, {loser} ignored."));
        }

        // ── Warning 8: unknown AgentExecutor value ───────────────────────────
        var rawExecutor = config["AgentExecutor"];
        if (!string.IsNullOrWhiteSpace(rawExecutor) &&
            !KnownAgentExecutors.Contains(rawExecutor.Trim().ToLowerInvariant()))
        {
            findings.Add(new Finding(Severity.Warning, "AgentExecutor",
                $"AgentExecutor='{rawExecutor}' is not recognized. " +
                $"Accepted values: {string.Join(", ", KnownAgentExecutors)}. " +
                "Falling back to auto-detect."));
        }

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
}
