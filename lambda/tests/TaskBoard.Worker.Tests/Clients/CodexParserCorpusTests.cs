using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Tests.Helpers;
using TaskBoard.Worker.Validation;

namespace TaskBoard.Worker.Tests.Clients;

/// <summary>
/// Parser regression suite for <see cref="CodexAgentExecutor.ParseStreamOutput"/>.
/// Each [Theory] case loads a captured stdout fixture from
/// <c>Fixtures/Cli/codex/{version}/</c> and asserts the parser extracts a
/// recognised outcome.
///
/// <para>
/// The whole point: when a new Codex CLI version ships and changes the wire
/// shape (as 0.125.0 did when it dropped top-level <c>structured_output</c>
/// events in favour of <c>agent_message</c> in <c>item.completed</c>), capture a
/// sample stdout into a new <c>{version}/</c> directory and either (a) confirm
/// the parser still works or (b) extend the parser and pin the new shape with
/// this corpus. Adding a new fixture file automatically adds a new test case
/// — no code change required (see <see cref="CodexFixtures"/>).
/// </para>
///
/// <para>See <c>docs/CliVersionTesting.md</c> for the full capture workflow.</para>
/// </summary>
public class CodexParserCorpusTests
{
    private readonly CodexAgentExecutor _executor;

    public CodexParserCorpusTests()
    {
        // ParseStreamOutput is an instance method (uses ILogger), so we need a
        // real instance — but we don't need ExecuteAsync to actually run, so the
        // delegate is a sentinel that throws if called.
        ProcessRunnerDelegate trapRunner =
            (_, _, _, _, _, _, _, _, _) => throw new InvalidOperationException(
                "ProcessRunner should not be invoked from a parser-corpus test.");

        _executor = new CodexAgentExecutor(
            Options.Create(new CodexCliLlmOptions()),
            NullLogger<CodexAgentExecutor>.Instance,
            trapRunner);
    }

    public static IEnumerable<object[]> CodexFixtures =>
        CliFixtureLoader.EnumerateFixtures("codex")
            .Select(f => new object[] { f.Version, f.Filename });

    [Theory]
    [MemberData(nameof(CodexFixtures))]
    public void ParseStreamOutput_ExtractsStructuredOutcome_FromVersionedFixture(
        string version, string filename)
    {
        var stdout = CliFixtureLoader.Read("codex", version, filename);

        var (resultJson, _) = _executor.ParseStreamOutput(stdout);

        Assert.NotNull(resultJson);

        var result = CodexAgentExecutor.ParseResult(resultJson);
        Assert.True(
            result.Outcome is AgentOutcome.COMPLETE
                or AgentOutcome.NEEDS_INFO
                or AgentOutcome.ERROR,
            $"Fixture {version}/{filename} parsed but produced an unexpected outcome: {result.Outcome}.");
    }

    /// <summary>
    /// Sanity guard: every CLI version directory the corpus knows about is
    /// either pinned in <see cref="CliVersionPolicy.KnownGood"/> or
    /// older than the policy's MinSupported (kept for legacy-shape regression
    /// coverage). Catches the case where someone adds a fixture for a new
    /// version but forgets to bump the policy.
    /// </summary>
    [Fact]
    public void EveryFixtureVersion_IsAcknowledgedByPolicy()
    {
        var policy = CliVersionPolicy.KnownGood[CliKey.Codex];

        var fixtureVersions = CliFixtureLoader.EnumerateFixtures("codex")
            .Select(f => f.Version)
            .Distinct()
            .ToList();

        Assert.NotEmpty(fixtureVersions);

        foreach (var versionStr in fixtureVersions)
        {
            var parsed = SemVer.TryParse(versionStr);
            Assert.NotNull(parsed);

            // The version is acknowledged if it falls inside the policy range
            // OR is below MinSupported (legacy-shape regression coverage we
            // intentionally keep parsing through). What we want to fail loudly
            // is a version ABOVE MaxKnown — that means someone shipped a
            // fixture without bumping the supported ceiling.
            if (policy.MaxKnown is not null && parsed.CompareTo(policy.MaxKnown) > 0)
            {
                Assert.Fail(
                    $"Fixture directory codex/{versionStr} is newer than CliVersionPolicy.KnownGood[codex].MaxKnown ({policy.MaxKnown}). " +
                    $"Bump MaxKnown to {parsed} after confirming the parser handles this version cleanly.");
            }
        }
    }
}
