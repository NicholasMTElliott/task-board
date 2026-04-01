using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Tests;

public class ProcessRunnerTests
{
    // ──────────────────────────────────────────────────────────────────────────
    // FormatArgsForLogging
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FormatArgsForLogging_EmptyArray_ReturnsEmptyString()
    {
        var result = ProcessRunner.FormatArgsForLogging([]);
        Assert.Equal("", result);
    }

    [Fact]
    public void FormatArgsForLogging_SingleArg_ReturnsArgAsIs()
    {
        var result = ProcessRunner.FormatArgsForLogging(["--print"]);
        Assert.Equal("--print", result);
    }

    [Fact]
    public void FormatArgsForLogging_MultipleArgs_JoinedWithSpaces()
    {
        var result = ProcessRunner.FormatArgsForLogging(["--model", "claude-sonnet-4-6", "--print"]);
        Assert.Equal("--model claude-sonnet-4-6 --print", result);
    }

    [Fact]
    public void FormatArgsForLogging_ArgWithSpaces_IsQuoted()
    {
        var result = ProcessRunner.FormatArgsForLogging(["exec", "do the task"]);
        Assert.Equal("exec \"do the task\"", result);
    }

    [Fact]
    public void FormatArgsForLogging_VeryLongArg_IsTruncated()
    {
        var longArg = new string('x', 3000);
        var result = ProcessRunner.FormatArgsForLogging([longArg]);

        Assert.True(result.Length < longArg.Length, "Long arg should be truncated");
        Assert.Contains("...[truncated]", result);
    }

    [Fact]
    public void FormatArgsForLogging_ArgExactly2000Chars_IsNotTruncated()
    {
        var arg = new string('a', 2000);
        var result = ProcessRunner.FormatArgsForLogging([arg]);

        Assert.DoesNotContain("...[truncated]", result);
        Assert.Equal(2000, result.Length);
    }

    [Fact]
    public void FormatArgsForLogging_ArgWith2001Chars_IsTruncated()
    {
        var arg = new string('a', 2001);
        var result = ProcessRunner.FormatArgsForLogging([arg]);

        Assert.Contains("...[truncated]", result);
    }
}
