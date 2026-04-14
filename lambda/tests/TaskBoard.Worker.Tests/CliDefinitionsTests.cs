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
}
