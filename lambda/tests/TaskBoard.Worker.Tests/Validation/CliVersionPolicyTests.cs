using TaskBoard.Worker.Validation;

namespace TaskBoard.Worker.Tests.Validation;

public class CliVersionPolicyTests
{
    // ── SemVer parsing ───────────────────────────────────────────────────────

    [Theory]
    [InlineData("0.125.0", 0, 125, 0)]
    [InlineData("codex-cli 0.125.0", 0, 125, 0)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("1.0.34 (Claude Code)", 1, 0, 34)]
    [InlineData("Claude Code v2.0.1-beta+build.5", 2, 0, 1)]
    public void SemVer_TryParse_ExtractsTriple(string input, int major, int minor, int patch)
    {
        var v = SemVer.TryParse(input);
        Assert.NotNull(v);
        Assert.Equal(new SemVer(major, minor, patch), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("(unknown: probe failed)")]
    [InlineData("not a version string")]
    [InlineData("1.2")] // no patch — not a SemVer triple
    public void SemVer_TryParse_RejectsInputsWithoutTriple(string input)
    {
        Assert.Null(SemVer.TryParse(input));
    }

    // ── SemVer ordering ──────────────────────────────────────────────────────

    [Fact]
    public void SemVer_OrdersByMajorThenMinorThenPatch()
    {
        var versions = new[]
        {
            new SemVer(0, 125, 0),
            new SemVer(0, 124, 99),
            new SemVer(1, 0, 0),
            new SemVer(0, 125, 1),
            new SemVer(2, 0, 0),
        };
        var sorted = versions.OrderBy(v => v).ToArray();
        Assert.Equal(
            new[]
            {
                new SemVer(0, 124, 99),
                new SemVer(0, 125, 0),
                new SemVer(0, 125, 1),
                new SemVer(1, 0, 0),
                new SemVer(2, 0, 0),
            },
            sorted);
    }

    // ── Policy classification ────────────────────────────────────────────────

    [Fact]
    public void Check_VersionWithinRange_ReturnsSupported()
    {
        var result = CliVersionPolicy.Check(CliKey.Codex, "0.125.0");
        Assert.Equal(CliVersionStatus.Supported, result.Status);
        Assert.Equal(new SemVer(0, 125, 0), result.ParsedVersion);
    }

    [Fact]
    public void Check_VersionBelowMinSupported_ReturnsOld()
    {
        // Codex MinSupported is 0.125.0 — anything older breaks the agent_message
        // wire shape contract this build expects.
        var result = CliVersionPolicy.Check(CliKey.Codex, "0.123.5");
        Assert.Equal(CliVersionStatus.Old, result.Status);
        Assert.Contains("upgrade the CLI", result.Message);
    }

    [Fact]
    public void Check_VersionAboveMaxKnown_ReturnsNewer()
    {
        // Anything newer than the highest version we've captured a fixture for
        // is "uncharted ground" — likely works, but we want operators to
        // capture a fixture and bump MaxKnown.
        var result = CliVersionPolicy.Check(CliKey.Codex, "0.999.0");
        Assert.Equal(CliVersionStatus.Newer, result.Status);
        Assert.Contains("Fixtures/Cli/codex/", result.Message);
        Assert.Contains("CliVersionPolicy.MaxKnown", result.Message);
    }

    // Note: a previous test (`Check_UnpinnedPolicy_ReturnsUnknown`) covered
    // the "policy entry exists but MinSupported and MaxKnown are both null"
    // branch in CliVersionPolicy.Check. That branch is currently unreachable
    // via production data — every CLI in KnownGood has a non-null MaxKnown
    // since the Dockerfile-pin guard rolled out. Re-add the test if a CLI is
    // ever stubbed back to null/null. The "no entry in dict" path is still
    // covered by Check_UnknownCliKey_ReturnsUnknown below.

    [Fact]
    public void Check_UnparseableVersionString_ReturnsUnknown()
    {
        var result = CliVersionPolicy.Check(CliKey.Codex, "(unknown: probe failed)");
        Assert.Equal(CliVersionStatus.Unknown, result.Status);
        Assert.Null(result.ParsedVersion);
    }

    [Fact]
    public void Check_UnknownCliKey_ReturnsUnknown()
    {
        var result = CliVersionPolicy.Check("aider", "1.0.0");
        Assert.Equal(CliVersionStatus.Unknown, result.Status);
        Assert.Contains("No version policy registered", result.Message);
    }

    // ── Policy inventory sanity ──────────────────────────────────────────────

    [Fact]
    public void KnownGood_CoversAllRegisteredCliKeys()
    {
        // Regression guard: every CliKey constant must have a policy entry,
        // even if it's unpinned (null/null). Forgetting one means the runtime
        // version check silently no-ops for that CLI.
        var registeredKeys = new[]
        {
            CliKey.Codex,
            CliKey.Claude,
            CliKey.OpenCode,
        };
        foreach (var key in registeredKeys)
        {
            Assert.True(
                CliVersionPolicy.KnownGood.ContainsKey(key),
                $"CliVersionPolicy.KnownGood is missing an entry for CLI key '{key}'.");
        }
    }
}
