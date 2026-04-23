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

    // ── Warning: section populated but provider doesn't match ───────────────

    [Fact]
    public void GithubSectionPopulated_WithoutProviderSet_ProducesWarning()
    {
        // Reproduces the user's exact bug: they set GitHubProjects:* but forgot
        // BoardProvider=github, so the section silently becomes dead weight.
        var findings = StartupConfigValidator.Validate(Build(
            ("GitHubProjects:Owner", "octo"),
            ("GitHubProjects:Repo", "widgets"),
            ("GitHubProjects:ProjectNumber", "4")));

        var warning = Assert.Single(findings, f =>
            f.Severity == StartupConfigValidator.Severity.Warning && f.Key == "GitHubProjects");
        Assert.Contains("BoardProvider", warning.Message);
    }

    [Fact]
    public void TrelloSectionPopulated_ButGithubSelected_ProducesWarning()
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
            f.Severity == StartupConfigValidator.Severity.Warning && f.Key == "Trello");
    }

    [Fact]
    public void BothSectionsPopulated_ProducesAmbiguityWarning()
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
            f.Severity == StartupConfigValidator.Severity.Warning && f.Key == "BoardProvider");
    }

    // ── Warning: unknown AgentExecutor ──────────────────────────────────────

    [Fact]
    public void UnknownAgentExecutor_ProducesWarning()
    {
        var findings = StartupConfigValidator.Validate(Build(
            ("BoardProvider", "stub"),
            ("AgentExecutor", "codexx")));

        Assert.Contains(findings, f =>
            f.Severity == StartupConfigValidator.Severity.Warning && f.Key == "AgentExecutor");
    }

    [Theory]
    [InlineData("stub")]
    [InlineData("claude-cli")]
    [InlineData("docker-claude-cli")]
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

    // ── Warning 9: legacy Docker section is deprecated ───────────────────────

    [Fact]
    public void LegacyDockerSection_Populated_ProducesWarning()
    {
        var findings = StartupConfigValidator.Validate(Build(
            ("Docker:ImageName", "custom:1.0")));

        Assert.Contains(findings, f =>
            f.Severity == StartupConfigValidator.Severity.Warning
            && f.Key == "Docker"
            && f.Message.Contains("deprecated"));
    }

    [Fact]
    public void LegacyDockerSection_AlsoWarns_WhenNewSectionIsAlsoSet()
    {
        // Regression guard: warning must fire even when both sections exist,
        // so operators are always nudged to clean up legacy config.
        var findings = StartupConfigValidator.Validate(Build(
            ("Docker:ImageName", "legacy:1.0"),
            ("DockerAgents:Claude:ImageName", "new:1.0")));

        Assert.Contains(findings, f =>
            f.Severity == StartupConfigValidator.Severity.Warning
            && f.Key == "Docker");
    }

    [Fact]
    public void LegacyDockerSection_Absent_NoWarning()
    {
        var findings = StartupConfigValidator.Validate(Build(
            ("DockerAgents:Claude:ImageName", "custom:1.0")));

        Assert.DoesNotContain(findings, f =>
            f.Key == "Docker" && f.Message.Contains("deprecated"));
    }

    // ── Warning 10: CodexCli:MaxBudgetUsd is unsupported ─────────────────────

    [Fact]
    public void CodexCliMaxBudgetUsd_Set_ProducesWarning()
    {
        var findings = StartupConfigValidator.Validate(Build(
            ("CodexCli:MaxBudgetUsd", "5.00")));

        Assert.Contains(findings, f =>
            f.Severity == StartupConfigValidator.Severity.Warning
            && f.Key == "CodexCli:MaxBudgetUsd");
    }

    [Fact]
    public void CodexCliMaxBudgetUsd_Absent_NoWarning()
    {
        var findings = StartupConfigValidator.Validate(Build(
            ("CodexCli:TimeoutSeconds", "600")));

        Assert.DoesNotContain(findings, f => f.Key == "CodexCli:MaxBudgetUsd");
    }

    [Fact]
    public void CodexCliMaxBudgetUsd_Empty_NoWarning()
    {
        var findings = StartupConfigValidator.Validate(Build(
            ("CodexCli:MaxBudgetUsd", "")));

        Assert.DoesNotContain(findings, f => f.Key == "CodexCli:MaxBudgetUsd");
    }
}
