using Microsoft.Extensions.Configuration;
using TaskBoard.Worker.Configuration;

namespace TaskBoard.Worker.Tests.Configuration;

public class StartupConfigValidatorTests
{
    private static IConfiguration Build(params (string Key, string? Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value))
            .Build();

    // ── Error: unknown BoardProvider ────────────────────────────────────────

    [Theory]
    [InlineData("gh")]
    [InlineData("jira")]
    [InlineData("GITHUB ")] // trimmed+lowered; still valid — this one should NOT error
    public void UnknownBoardProvider_ProducesError(string provider)
    {
        var findings = StartupConfigValidator.Validate(Build(("BoardProvider", provider)));

        if (provider.Trim().ToLowerInvariant() is "github")
        {
            Assert.DoesNotContain(findings, f =>
                f.Severity == StartupConfigValidator.Severity.Error && f.Key == "BoardProvider");
        }
        else
        {
            Assert.Contains(findings, f =>
                f.Severity == StartupConfigValidator.Severity.Error && f.Key == "BoardProvider");
        }
    }

    [Fact]
    public void EmptyBoardProvider_IsTreatedAsStub_NoUnknownError()
    {
        var findings = StartupConfigValidator.Validate(Build(("BoardProvider", "")));

        Assert.DoesNotContain(findings, f =>
            f.Severity == StartupConfigValidator.Severity.Error && f.Key == "BoardProvider");
    }

    // ── Error: github selected but fields missing ───────────────────────────

    [Fact]
    public void GithubSelected_WithNoFields_ListsAllThreeMissingKeys()
    {
        var findings = StartupConfigValidator.Validate(Build(("BoardProvider", "github")));

        var error = Assert.Single(findings, f =>
            f.Severity == StartupConfigValidator.Severity.Error && f.Key == "GitHubProjects");
        Assert.Contains("Owner", error.Message);
        Assert.Contains("Repo", error.Message);
        Assert.Contains("ProjectNumber", error.Message);
    }

    [Fact]
    public void GithubSelected_WithAllFields_NoError()
    {
        var findings = StartupConfigValidator.Validate(Build(
            ("BoardProvider", "github"),
            ("GitHubProjects:Owner", "octo"),
            ("GitHubProjects:Repo", "widgets"),
            ("GitHubProjects:ProjectNumber", "4")));

        Assert.DoesNotContain(findings, f => f.Severity == StartupConfigValidator.Severity.Error);
    }

    // ── Error: trello selected but fields missing ───────────────────────────

    [Fact]
    public void TrelloSelected_WithNoFields_ListsAllMissingKeys()
    {
        var findings = StartupConfigValidator.Validate(Build(("BoardProvider", "trello")));

        var error = Assert.Single(findings, f =>
            f.Severity == StartupConfigValidator.Severity.Error && f.Key == "Trello");
        Assert.Contains("Trello:ApiKey", error.Message);
        Assert.Contains("Trello:ApiToken", error.Message);
        Assert.Contains("Trello:BoardId", error.Message);
    }

    // ── Error: section populated but provider doesn't match ────────────────

    [Fact]
    public void GithubSectionPopulated_WithoutProviderSet_ProducesError()
    {
        // KvA-class footgun: GitHubProjects:* set without BoardProvider=github
        // would silently become dead weight. Promoted to Error so operator must
        // explicitly resolve the contradiction at startup.
        var findings = StartupConfigValidator.Validate(Build(
            ("GitHubProjects:Owner", "octo"),
            ("GitHubProjects:Repo", "widgets"),
            ("GitHubProjects:ProjectNumber", "4")));

        var error = Assert.Single(findings, f =>
            f.Severity == StartupConfigValidator.Severity.Error && f.Key == "GitHubProjects");
        Assert.Contains("BoardProvider", error.Message);
    }

    [Fact]
    public void TrelloSectionPopulated_ButGithubSelected_ProducesError()
    {
        var findings = StartupConfigValidator.Validate(Build(
            ("BoardProvider", "github"),
            ("GitHubProjects:Owner", "octo"),
            ("GitHubProjects:Repo", "widgets"),
            ("GitHubProjects:ProjectNumber", "4"),
            ("Trello:ApiKey", "key"),
            ("Trello:ApiToken", "token"),
            ("Trello:BoardId", "abc")));

        Assert.Contains(findings, f =>
            f.Severity == StartupConfigValidator.Severity.Error && f.Key == "Trello");
    }

    [Fact]
    public void BothSectionsPopulated_ProducesAmbiguityError()
    {
        var findings = StartupConfigValidator.Validate(Build(
            ("BoardProvider", "github"),
            ("GitHubProjects:Owner", "octo"),
            ("GitHubProjects:Repo", "widgets"),
            ("GitHubProjects:ProjectNumber", "4"),
            ("Trello:ApiKey", "key"),
            ("Trello:ApiToken", "token"),
            ("Trello:BoardId", "abc")));

        Assert.Contains(findings, f =>
            f.Severity == StartupConfigValidator.Severity.Error && f.Key == "BoardProvider");
    }

    // ── Error: unknown AgentExecutor ────────────────────────────────────────

    [Fact]
    public void UnknownAgentExecutor_ProducesError()
    {
        var findings = StartupConfigValidator.Validate(Build(
            ("BoardProvider", "stub"),
            ("AgentExecutor", "codexx")));

        Assert.Contains(findings, f =>
            f.Severity == StartupConfigValidator.Severity.Error && f.Key == "AgentExecutor");
    }

    [Theory]
    [InlineData("stub")]
    [InlineData("claude-cli")]
    [InlineData("docker-claude-cli")]
    [InlineData("docker-codex")]
    [InlineData("docker-opencode")]
    [InlineData("docker-claude-qwen")]
    [InlineData("codex")]
    public void KnownAgentExecutorValues_DoNotWarn(string executor)
    {
        var findings = StartupConfigValidator.Validate(Build(
            ("BoardProvider", "stub"),
            ("AgentExecutor", executor)));

        Assert.DoesNotContain(findings, f => f.Key == "AgentExecutor");
    }

    // ── Negative cases: things we chose not to flag ─────────────────────────

    [Fact]
    public void StubProviderWithNoOtherConfig_ProducesNoFindings()
    {
        // The zero-config dev flow must remain clean.
        var findings = StartupConfigValidator.Validate(Build());

        Assert.Empty(findings);
    }

    [Fact]
    public void MissingWorktreeBasePath_IsNotFlagged()
    {
        var findings = StartupConfigValidator.Validate(Build(("BoardProvider", "stub")));

        Assert.DoesNotContain(findings, f => f.Key.Contains("Worktree"));
    }

    [Fact]
    public void MissingDatabaseConnectionString_IsNotFlaggedHere()
    {
        // Handled elsewhere as a distinct warning about run-tracking being disabled.
        var findings = StartupConfigValidator.Validate(Build(("BoardProvider", "stub")));

        Assert.DoesNotContain(findings, f => f.Key.Contains("Database"));
    }

    // ── LogAndMaybeExit semantics ───────────────────────────────────────────

    [Fact]
    public void LogAndMaybeExit_ReturnsTrue_WhenOnlyWarnings()
    {
        var findings = new[]
        {
            new StartupConfigValidator.Finding(
                StartupConfigValidator.Severity.Warning, "K", "msg"),
        };

        Assert.True(StartupConfigValidator.LogAndMaybeExit(
            findings, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
    }

    [Fact]
    public void LogAndMaybeExit_ReturnsFalse_WhenAnyError()
    {
        var findings = new[]
        {
            new StartupConfigValidator.Finding(
                StartupConfigValidator.Severity.Warning, "K1", "w"),
            new StartupConfigValidator.Finding(
                StartupConfigValidator.Severity.Error, "K2", "e"),
        };

        Assert.False(StartupConfigValidator.LogAndMaybeExit(
            findings, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
    }

    // ── Error 9: legacy Docker section is deprecated ─────────────────────────

    [Fact]
    public void LegacyDockerSection_Populated_ProducesError()
    {
        // KvA-class footgun: legacy Docker section ONLY binds to Claude executor.
        // An operator who sets Docker:ImageName expecting it to apply to all
        // Docker executors gets a silently-partial result. Force migration.
        var findings = StartupConfigValidator.Validate(Build(
            ("Docker:ImageName", "custom:1.0")));

        var error = Assert.Single(findings, f =>
            f.Severity == StartupConfigValidator.Severity.Error
            && f.Key == "Docker");
        Assert.Contains("deprecated", error.Message);
        // Hint must call out the per-executor split for OpenCode/ClaudeQwen.
        Assert.Contains("docker-opencode", error.Message);
        Assert.Contains("docker-claude-qwen", error.Message);
    }

    [Fact]
    public void LegacyDockerSection_AlsoErrors_WhenNewSectionIsAlsoSet()
    {
        // Regression guard: error must fire even when both sections exist,
        // so operators always clean up legacy config (no silent precedence).
        var findings = StartupConfigValidator.Validate(Build(
            ("Docker:ImageName", "legacy:1.0"),
            ("DockerAgents:Claude:ImageName", "new:1.0")));

        Assert.Contains(findings, f =>
            f.Severity == StartupConfigValidator.Severity.Error
            && f.Key == "Docker");
    }

    [Fact]
    public void LegacyDockerSection_Absent_NoFinding()
    {
        var findings = StartupConfigValidator.Validate(Build(
            ("DockerAgents:Claude:ImageName", "custom:1.0")));

        Assert.DoesNotContain(findings, f => f.Key == "Docker");
    }

    // ── Error 10: CodexCli:MaxBudgetUsd is unsupported ───────────────────────

    [Fact]
    public void CodexCliMaxBudgetUsd_Set_ProducesError()
    {
        var findings = StartupConfigValidator.Validate(Build(
            ("CodexCli:MaxBudgetUsd", "5.00")));

        Assert.Contains(findings, f =>
            f.Severity == StartupConfigValidator.Severity.Error
            && f.Key == "CodexCli:MaxBudgetUsd");
    }

    [Fact]
    public void CodexCliMaxBudgetUsd_Absent_NoFinding()
    {
        var findings = StartupConfigValidator.Validate(Build(
            ("CodexCli:TimeoutSeconds", "600")));

        Assert.DoesNotContain(findings, f => f.Key == "CodexCli:MaxBudgetUsd");
    }

    [Fact]
    public void CodexCliMaxBudgetUsd_Empty_NoFinding()
    {
        var findings = StartupConfigValidator.Validate(Build(
            ("CodexCli:MaxBudgetUsd", "")));

        Assert.DoesNotContain(findings, f => f.Key == "CodexCli:MaxBudgetUsd");
    }

    // ── Error 11: Docker ContainerUser root is unsupported ──────────────────

    [Theory]
    [InlineData("DockerAgents:Claude:ContainerUser", "root")]
    [InlineData("DockerAgents:ClaudeQwen:ContainerUser", "0")]
    [InlineData("DockerAgents:Codex:ContainerUser", "0:0")]
    [InlineData("DockerAgents:OpenCode:ContainerUser", "ROOT")]
    public void DockerContainerUserRoot_ProducesError(string key, string value)
    {
        var findings = StartupConfigValidator.Validate(Build((key, value)));

        var error = Assert.Single(findings, f =>
            f.Severity == StartupConfigValidator.Severity.Error
            && f.Key == key);
        Assert.Contains("GroupAdd", error.Message);
        Assert.Contains("root", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("DockerAgents:Claude:ContainerUser", "agent")]
    [InlineData("DockerAgents:Codex:ContainerUser", "1000:1000")]
    public void DockerContainerUserNonRoot_NoFinding(string key, string value)
    {
        var findings = StartupConfigValidator.Validate(Build((key, value)));

        Assert.DoesNotContain(findings, f => f.Key == key);
    }

    // ── Error 12: Docker PerformanceVolumes paths are workspace-local ───────

    [Theory]
    [InlineData("DockerAgents:Claude:PerformanceVolumes:0", "../node_modules")]
    [InlineData("DockerAgents:Codex:PerformanceVolumes:0", "/absolute")]
    [InlineData("DockerAgents:OpenCode:PerformanceVolumes:0", ".git")]
    [InlineData("DockerAgents:ClaudeQwen:PerformanceVolumes:0", "C:/cache")]
    public void DockerPerformanceVolumesUnsafePath_ProducesError(string key, string value)
    {
        var findings = StartupConfigValidator.Validate(Build((key, value)));

        var error = Assert.Single(findings, f =>
            f.Severity == StartupConfigValidator.Severity.Error
            && f.Key == key);
        Assert.Contains("Invalid Docker performance volume path", error.Message);
    }

    [Fact]
    public void DockerPerformanceVolumesSafePaths_NoFinding()
    {
        var findings = StartupConfigValidator.Validate(Build(
            ("DockerAgents:Claude:PerformanceVolumes:0", "node_modules"),
            ("DockerAgents:Claude:PerformanceVolumes:1", ".pnpm-store")));

        Assert.DoesNotContain(findings, f => f.Key.StartsWith("DockerAgents:Claude:PerformanceVolumes"));
    }

    // ── Cross-cutting: KvA exact reproduction ────────────────────────────────

    [Fact]
    public void KvAExactConfig_LegacyDockerWithDockerAgents_FailsStartup()
    {
        // The exact KvA setup that was the trigger for promoting these to errors:
        // legacy Docker.ImageName + DockerAgents:Claude/OpenCode/ClaudeQwen all set,
        // BoardProvider=github, AgentExecutor=docker-claude-cli.
        // Pre-fix: one warning that scrolled away in polling startup.
        // Post-fix: blocking error with migration hint covering all three executors.
        var findings = StartupConfigValidator.Validate(Build(
            ("BoardProvider", "github"),
            ("AgentExecutor", "docker-claude-cli"),
            ("Docker:ImageName", "aiboard-agent-sandbox:godot"),
            ("DockerAgents:Claude:TimeoutSeconds", "1800"),
            ("DockerAgents:OpenCode:TimeoutSeconds", "3600"),
            ("DockerAgents:ClaudeQwen:TimeoutSeconds", "3600"),
            ("GitHubProjects:Owner", "NicholasMTElliott"),
            ("GitHubProjects:Repo", "NicholasMTElliott/kva"),
            ("GitHubProjects:ProjectNumber", "4")));

        Assert.False(StartupConfigValidator.LogAndMaybeExit(
            findings, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance));
        Assert.Contains(findings, f =>
            f.Severity == StartupConfigValidator.Severity.Error && f.Key == "Docker");
    }
}
