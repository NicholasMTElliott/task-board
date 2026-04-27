using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Tests.Helpers;
using TaskBoard.Worker.Validation;

namespace TaskBoard.Worker.Tests.Clients;

/// <summary>
/// Parser regression suite for <see cref="OpenCodeOutputParser"/>. Each
/// [Theory] case loads a captured stdout fixture from
/// <c>Fixtures/Cli/opencode/{version}/</c> and asserts the parser extracts
/// a structured outcome.
///
/// <para>
/// Unlike Claude (NDJSON stream) and Codex (NDJSON with envelope), OpenCode
/// emits free-form assistant text and the parser tries fenced JSON →
/// whole-document JSON → trailing balanced object. The fixtures cover one
/// of each strategy so a regression in any path fails loudly.
/// </para>
///
/// <para>
/// No live smoke test is included because OpenCode runs inside the
/// docker-opencode sandbox image against a local llama.cpp server — the
/// prerequisites (Docker daemon + llm-net + a running llama-server) are
/// heavy enough that gating is best handled by the operator running the
/// existing <c>scripts/smoke-opencode.ps1</c> when ready.
/// </para>
/// </summary>
public class OpenCodeParserCorpusTests
{
    public static IEnumerable<object[]> OpenCodeFixtures =>
        CliFixtureLoader.EnumerateFixtures("opencode")
            .Select(f => new object[] { f.Version, f.Filename });

    [Theory]
    [MemberData(nameof(OpenCodeFixtures))]
    public void Parse_ExtractsStructuredOutcome_FromVersionedFixture(
        string version, string filename)
    {
        var stdout = CliFixtureLoader.Read("opencode", version, filename);

        var (resultJson, _) = OpenCodeOutputParser.Parse(stdout, NullLogger.Instance);

        Assert.NotNull(resultJson);

        // Wrap so the shared parser path is exercised, mirroring how
        // DockerOpenCodeAgentExecutor consumes the result.
        var wrapped = "{\"structured_output\":" + resultJson + "}";
        var result = AgentOutputParser.ParseResult(wrapped);

        Assert.True(
            result.Outcome is AgentOutcome.COMPLETE
                or AgentOutcome.NEEDS_INFO
                or AgentOutcome.ERROR,
            $"Fixture {version}/{filename} parsed but produced an unexpected outcome: {result.Outcome}.");
    }

    /// <summary>
    /// Fixture-vs-policy guard. See <see cref="CodexParserCorpusTests"/> for
    /// rationale. OpenCode policy is currently unpinned (the version is
    /// effectively the sandbox image's baked CLI), so this is a no-op until
    /// the operator decides to pin a version.
    /// </summary>
    [Fact]
    public void EveryFixtureVersion_IsAcknowledgedByPolicy()
    {
        var policy = CliVersionPolicy.KnownGood[CliKey.OpenCode];

        var fixtureVersions = CliFixtureLoader.EnumerateFixtures("opencode")
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
                    $"Fixture directory opencode/{versionStr} is newer than CliVersionPolicy.KnownGood[opencode].MaxKnown ({policy.MaxKnown}). " +
                    $"Bump MaxKnown to {parsed} after confirming the parser handles this version cleanly.");
            }
        }
    }
}
