using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Tests.Helpers;
using TaskBoard.Worker.Validation;

namespace TaskBoard.Worker.Tests.Clients;

/// <summary>
/// Parser regression suite for <see cref="AgentOutputParser.ParseStreamOutput"/>
/// against captured Claude CLI <c>--output-format stream-json</c> stdout. Each
/// [Theory] case loads a fixture from <c>Fixtures/Cli/claude/{version}/</c>
/// and asserts the parser extracts a structured outcome.
///
/// <para>
/// Mirrors <see cref="CodexParserCorpusTests"/> for the Claude CLI. Adding a
/// new fixture file under a versioned directory automatically adds a new test
/// case. See <c>docs/CliVersionTesting.md</c> for the capture workflow.
/// </para>
/// </summary>
public class ClaudeParserCorpusTests
{
    public static IEnumerable<object[]> ClaudeFixtures =>
        CliFixtureLoader.EnumerateFixtures("claude")
            .Select(f => new object[] { f.Version, f.Filename });

    [Theory]
    [MemberData(nameof(ClaudeFixtures))]
    public void ParseStreamOutput_ExtractsStructuredOutcome_FromVersionedFixture(
        string version, string filename)
    {
        var stdout = CliFixtureLoader.Read("claude", version, filename);

        var (resultJson, _) = AgentOutputParser.ParseStreamOutput(
            stdout, NullLogger.Instance);

        Assert.NotNull(resultJson);

        var result = AgentOutputParser.ParseResult(resultJson);
        Assert.True(
            result.Outcome is AgentOutcome.COMPLETE
                or AgentOutcome.NEEDS_INFO
                or AgentOutcome.ERROR,
            $"Fixture {version}/{filename} parsed but produced an unexpected outcome: {result.Outcome}.");
    }

    /// <summary>
    /// Fixture-vs-policy guard. See <see cref="CodexParserCorpusTests"/> for the
    /// rationale. For Claude the policy is currently unpinned (MaxKnown=null),
    /// so this is a no-op until the operator decides to pin a Claude version.
    /// </summary>
    [Fact]
    public void EveryFixtureVersion_IsAcknowledgedByPolicy()
    {
        var policy = CliVersionPolicy.KnownGood[CliKey.Claude];

        var fixtureVersions = CliFixtureLoader.EnumerateFixtures("claude")
            .Select(f => f.Version)
            .Distinct()
            .ToList();

        Assert.NotEmpty(fixtureVersions);

        foreach (var versionStr in fixtureVersions)
        {
            var parsed = SemVer.TryParse(versionStr);
            Assert.NotNull(parsed);

            if (policy.MaxKnown is not null && parsed.CompareTo(policy.MaxKnown) > 0)
            {
                Assert.Fail(
                    $"Fixture directory claude/{versionStr} is newer than CliVersionPolicy.KnownGood[claude].MaxKnown ({policy.MaxKnown}). " +
                    $"Bump MaxKnown to {parsed} after confirming the parser handles this version cleanly.");
            }
        }
    }
}
