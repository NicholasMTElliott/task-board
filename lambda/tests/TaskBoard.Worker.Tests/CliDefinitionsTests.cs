using TaskBoard.Worker;

namespace TaskBoard.Worker.Tests;

public class CliDefinitionsTests
{
    // ── ResolvePath: dot-prefixed (CWD-relative) ─────────────────────────────

    [Fact]
    public void ResolvePath_DotSlash_ResolvesRelativeToCwd()
    {
        var result = CliDefinitions.ResolvePath("./config.json");
        var expected = Path.GetFullPath("./config.json");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolvePath_DotDotSlash_ResolvesRelativeToCwd()
    {
        var result = CliDefinitions.ResolvePath("../config.json");
        var expected = Path.GetFullPath("../config.json");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolvePath_DotAlone_ResolvesToCwd()
    {
        var result = CliDefinitions.ResolvePath(".");
        var expected = Path.GetFullPath(".");
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolvePath_DotWithBackslash_ResolvesRelativeToCwd()
    {
        var result = CliDefinitions.ResolvePath(@".\subdir\file.json");
        var expected = Path.GetFullPath(@".\subdir\file.json");
        Assert.Equal(expected, result);
    }

    // ── ResolvePath: absolute paths ──────────────────────────────────────────

    [Fact]
    public void ResolvePath_AbsolutePath_ReturnedNormalized()
    {
        // Use a platform-appropriate absolute path
        var input = OperatingSystem.IsWindows()
            ? @"C:\content\workflow.json"
            : "/opt/content/workflow.json";
        var result = CliDefinitions.ResolvePath(input);
        Assert.Equal(Path.GetFullPath(input), result);
    }

    [Fact]
    public void ResolvePath_AbsoluteWithDotDot_Normalized()
    {
        var input = OperatingSystem.IsWindows()
            ? @"C:\content\sub\..\workflow.json"
            : "/opt/content/sub/../workflow.json";
        var result = CliDefinitions.ResolvePath(input);
        var expected = OperatingSystem.IsWindows()
            ? @"C:\content\workflow.json"
            : "/opt/content/workflow.json";
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolvePath_UncPath_TreatedAsAbsolute()
    {
        if (!OperatingSystem.IsWindows()) return; // UNC is Windows-only

        var result = CliDefinitions.ResolvePath(@"\\server\share\file.json");
        Assert.Equal(@"\\server\share\file.json", result);
    }

    // ── ResolvePath: bare paths (exe-relative) ───────────────────────────────

    [Fact]
    public void ResolvePath_BareName_ResolvesRelativeToExeDir()
    {
        var result = CliDefinitions.ResolvePath("workflow.json");
        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "workflow.json"));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolvePath_BareSubdir_ResolvesRelativeToExeDir()
    {
        var result = CliDefinitions.ResolvePath("content/prompts");
        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "content/prompts"));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolvePath_BareWithBackslash_ResolvesRelativeToExeDir()
    {
        var result = CliDefinitions.ResolvePath(@"content\prompts\file.md");
        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"content\prompts\file.md"));
        Assert.Equal(expected, result);
    }

    // ── ResolvePath: edge cases ──────────────────────────────────────────────

    [Fact]
    public void ResolvePath_ForwardSlashPath_NotTreatedAsDotPrefix()
    {
        // "subdir/file" should be exe-relative, not CWD-relative
        var result = CliDefinitions.ResolvePath("subdir/file.json");
        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "subdir/file.json"));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void ResolvePath_ResultIsAlwaysAbsolute()
    {
        Assert.True(Path.IsPathRooted(CliDefinitions.ResolvePath(".")));
        Assert.True(Path.IsPathRooted(CliDefinitions.ResolvePath("bare.json")));
        Assert.True(Path.IsPathRooted(CliDefinitions.ResolvePath("./relative.json")));
    }

    // ── ShouldShowHelp ───────────────────────────────────────────────────────

    [Fact]
    public void ShouldShowHelp_NoArgs_ReturnsFalse()
    {
        // No-args is now a valid invocation (config sources supply Mode etc.).
        // Only explicit help flags should trigger the help screen.
        Assert.False(CliDefinitions.ShouldShowHelp([]));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-?")]
    public void ShouldShowHelp_HelpFlags_ReturnsTrue(string flag)
    {
        Assert.True(CliDefinitions.ShouldShowHelp(["--mode", "agent", flag]));
    }

    [Fact]
    public void ShouldShowHelp_ModeOnly_ReturnsFalse()
    {
        Assert.False(CliDefinitions.ShouldShowHelp(["--mode", "polling"]));
    }

    // ── SwitchMappings ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("--config", "ConfigPath")]
    [InlineData("--prompt-root", "PromptRoot")]
    [InlineData("--workflow-config", "WorkflowConfigPath")]
    [InlineData("--workspace", "AgentWorkspacePath")]
    [InlineData("--worktree-base", "WorktreeBasePath")]
    [InlineData("--mode", "Mode")]
    [InlineData("--card-id", "CardId")]
    [InlineData("--board-provider", "BoardProvider")]
    [InlineData("--github-repo", "GitHubProjects:Repo")]
    [InlineData("--claude-path", "ClaudeCli:ExecutablePath")]
    public void SwitchMappings_ContainsExpectedEntries(string flag, string configKey)
    {
        Assert.True(CliDefinitions.SwitchMappings.ContainsKey(flag));
        Assert.Equal(configKey, CliDefinitions.SwitchMappings[flag]);
    }

    [Fact]
    public void SwitchMappings_IsCaseInsensitive()
    {
        Assert.True(CliDefinitions.SwitchMappings.ContainsKey("--MODE"));
        Assert.True(CliDefinitions.SwitchMappings.ContainsKey("--Workflow-Config"));
    }

    // ── ValidateKnownFlags ────────────────────────────────────────────────────

    [Fact]
    public void ValidateKnownFlags_AllRecognised_ReturnsEmpty()
    {
        var report = CliDefinitions.ValidateKnownFlags(
            ["--mode", "polling", "--board-id", "1", "--workspace", "."]);
        Assert.Empty(report.UnknownFlags);
    }

    [Fact]
    public void ValidateKnownFlags_HelpForm_NotRejected()
    {
        var report = CliDefinitions.ValidateKnownFlags(["--help"]);
        Assert.Empty(report.UnknownFlags);

        var report2 = CliDefinitions.ValidateKnownFlags(["-h"]);
        Assert.Empty(report2.UnknownFlags);

        var report3 = CliDefinitions.ValidateKnownFlags(["-?"]);
        Assert.Empty(report3.UnknownFlags);
    }

    [Fact]
    public void ValidateKnownFlags_UnknownFlag_AppearsInReport()
    {
        var report = CliDefinitions.ValidateKnownFlags(["--definitely-not-real", "value"]);
        Assert.Contains("--definitely-not-real", report.UnknownFlags);
    }

    [Fact]
    public void ValidateKnownFlags_ModeValidationTypo_SuggestsModeValidation()
    {
        // the exact v0.0.15 typo: typed --validate, expected --mode validation.
        var report = CliDefinitions.ValidateKnownFlags(["--validate"]);
        Assert.Contains("--validate", report.UnknownFlags);
        Assert.True(report.TypoHints.TryGetValue("--validate", out var hint));
        Assert.Equal("--mode validation", hint);
    }

    [Fact]
    public void ValidateKnownFlags_NameEqualsValueForm_RecognisesFlag()
    {
        // --mode=polling is shorthand for --mode polling — the `=value` half is
        // not a separate flag and should not trigger an unknown-flag rejection.
        var report = CliDefinitions.ValidateKnownFlags(["--mode=polling"]);
        Assert.Empty(report.UnknownFlags);
    }

    [Fact]
    public void ValidateKnownFlags_PositionalValuesIgnored()
    {
        // Positional (non-flag) tokens shouldn't be rejected even if they
        // happen to be unrecognised — only `-`-prefixed tokens are scrutinised.
        var report = CliDefinitions.ValidateKnownFlags(
            ["--mode", "agent", "some-positional-word"]);
        Assert.Empty(report.UnknownFlags);
    }

    // ── --unsafe flag ────────────────────────────────────────────────────────

    [Fact]
    public void SwitchMappings_UnsafeFlag_MapsToUnsafeKey()
    {
        Assert.True(CliDefinitions.SwitchMappings.ContainsKey("--unsafe"));
        Assert.Equal("Unsafe", CliDefinitions.SwitchMappings["--unsafe"]);
    }

    [Fact]
    public void BareBooleanFlags_IncludesUnsafe()
    {
        Assert.Contains("--unsafe", CliDefinitions.BareBooleanFlags);
    }

    [Fact]
    public void NormalizeBareBooleanFlags_BareUnsafe_RewrittenToTrue()
    {
        var input = new[] { "--mode", "polling", "--unsafe" };
        var result = CliDefinitions.NormalizeBareBooleanFlags(input);

        Assert.Equal("--mode", result[0]);
        Assert.Equal("polling", result[1]);
        Assert.Equal("--unsafe=true", result[2]);
    }

    [Fact]
    public void ValidateKnownFlags_BareUnsafe_NotRejected()
    {
        // Bare --unsafe must validate before normalisation.
        var report = CliDefinitions.ValidateKnownFlags(["--mode", "polling", "--unsafe"]);
        Assert.Empty(report.UnknownFlags);
    }

    [Fact]
    public void ValidateKnownFlags_UnsafeEqualsTrue_NotRejected()
    {
        var report = CliDefinitions.ValidateKnownFlags(["--mode", "polling", "--unsafe=true"]);
        Assert.Empty(report.UnknownFlags);
    }

    // ── --install flag ───────────────────────────────────────────────────────

    [Fact]
    public void SwitchMappings_InstallFlag_MapsToInstallKey()
    {
        Assert.True(CliDefinitions.SwitchMappings.ContainsKey("--install"));
        Assert.Equal("Install", CliDefinitions.SwitchMappings["--install"]);
    }

    [Fact]
    public void BareBooleanFlags_IncludesInstall()
    {
        Assert.Contains("--install", CliDefinitions.BareBooleanFlags);
    }

    [Fact]
    public void NormalizeBareBooleanFlags_BareInstall_RewrittenToTrue()
    {
        var input = new[] { "--install" };
        var result = CliDefinitions.NormalizeBareBooleanFlags(input);
        Assert.Equal("--install=true", result[0]);
    }

    [Fact]
    public void NormalizeBareBooleanFlags_InstallWithMode_BothPreserved()
    {
        var input = new[] { "--install", "--mode", "polling" };
        var result = CliDefinitions.NormalizeBareBooleanFlags(input);
        Assert.Equal("--install=true", result[0]);
        Assert.Equal("--mode", result[1]);
        Assert.Equal("polling", result[2]);
    }

    [Fact]
    public void ValidateKnownFlags_BareInstall_NotRejected()
    {
        var report = CliDefinitions.ValidateKnownFlags(["--install"]);
        Assert.Empty(report.UnknownFlags);
    }
}
