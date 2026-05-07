using System.Text.RegularExpressions;
using TaskBoard.Worker.Validation;

namespace TaskBoard.Worker.Tests.Validation;

/// <summary>
/// CI guard: fails the build when a Dockerfile's pinned CLI version drifts
/// ahead of <see cref="CliVersionPolicy.KnownGood"/>'s <c>MaxKnown</c> for
/// the matching CLI. Forces the upgrade dance (capture fixture → run
/// parser-corpus tests → bump policy → bump Dockerfile) to stay in sync.
///
/// <para>
/// Failure here means one of:
/// </para>
/// <list type="bullet">
///   <item>Operator bumped a Dockerfile pin without bumping <c>MaxKnown</c>
///         (and presumably without capturing a fixture). Bump the policy
///         and add a fixture under <c>Fixtures/Cli/{cli}/{newVersion}/</c>.</item>
///   <item>Dockerfile uses <c>latest</c> (this guard requires a concrete pin).</item>
///   <item>Dockerfile's ARG default isn't matched by our regex; either the
///         Dockerfile shape changed or a new CLI was added without updating
///         the test's <c>CliPinSources</c> table.</item>
/// </list>
///
/// <para>See <c>docs/CliVersionTesting.md</c> for the full bump dance and
/// <c>scripts/check-cli-versions.ps1</c> for the npm-poll companion.</para>
/// </summary>
public class DockerfilePolicyDriftTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    /// <summary>
    /// Per-CLI mapping of <c>(policyKey, dockerfileRelativePath, dockerfileArgName)</c>.
    /// Adding a new sandbox CLI? Register the policy entry in
    /// <see cref="CliVersionPolicy.KnownGood"/> AND add a row here so the
    /// guard covers it.
    /// </summary>
    public static IEnumerable<object[]> CliPinSources()
    {
        yield return new object[] { CliKey.Claude, "docker/agent-sandbox/Dockerfile", "CLAUDE_CLI_VERSION" };
        yield return new object[] { CliKey.Codex, "docker/codex-sandbox/Dockerfile", "CODEX_CLI_VERSION" };
        yield return new object[] { CliKey.OpenCode, "docker/opencode-sandbox/Dockerfile", "OPENCODE_CLI_VERSION" };
    }

    [Theory]
    [MemberData(nameof(CliPinSources))]
    public void DockerfilePin_DoesNotExceedPolicyMaxKnown(string cliKey, string dockerfileRelPath, string argName)
    {
        var dockerfilePath = Path.Combine(RepoRoot, dockerfileRelPath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(dockerfilePath), $"Dockerfile not found at {dockerfilePath}");

        var pinnedRaw = ReadDockerfileArgDefault(dockerfilePath, argName);
        Assert.NotNull(pinnedRaw);
        if (pinnedRaw == "latest")
        {
            Assert.Fail(
                $"Dockerfile {dockerfileRelPath} uses 'latest' for {argName} — pin to a concrete version. " +
                "Bleeding-edge defaults bit us repeatedly with silent wire-shape changes.");
        }

        var pinned = SemVer.TryParse(pinnedRaw);
        Assert.True(pinned is not null,
            $"Could not parse a SemVer from Dockerfile pin '{pinnedRaw}' for {argName}");

        Assert.True(CliVersionPolicy.KnownGood.TryGetValue(cliKey, out var range),
            $"No CliVersionPolicy entry for '{cliKey}'");

        // The whole point of the guard: Dockerfile pin must NEVER exceed MaxKnown.
        // If it does, we're shipping a sandbox image with a CLI version we never
        // wrote a fixture for — exactly the silent-failure mode this is
        // designed to prevent.
        if (range.MaxKnown is null)
        {
            Assert.Fail(
                $"CliVersionPolicy.KnownGood[\"{cliKey}\"].MaxKnown is null but Dockerfile pins {pinnedRaw}. " +
                $"Pin the policy to {pinned} (and capture a fixture under " +
                $"Fixtures/Cli/{cliKey}/{pinned}/) — see docs/CliVersionTesting.md.");
        }

        Assert.True(pinned!.CompareTo(range.MaxKnown) <= 0,
            $"Dockerfile {dockerfileRelPath} pins {argName}={pinned}, but " +
            $"CliVersionPolicy.KnownGood[\"{cliKey}\"].MaxKnown is {range.MaxKnown}. " +
            $"Bump the policy to {pinned} (and capture a fixture at " +
            $"Fixtures/Cli/{cliKey}/{pinned}/) before bumping the Dockerfile. " +
            $"See docs/CliVersionTesting.md for the full bump dance.");
    }

    [Theory]
    [MemberData(nameof(CliPinSources))]
    public void DockerfilePin_AndPolicyMaxKnown_AgreeOnExactVersion(string cliKey, string dockerfileRelPath, string argName)
    {
        // Tighter sibling check to the one above. The "<= MaxKnown" rule
        // permits the policy to be ahead of the Dockerfile (e.g., we've
        // captured fixtures for a future version but haven't rolled out the
        // image yet). That's fine for correctness but creates a quiet
        // mismatch operators should know about. This second test is informational
        // — it surfaces the mismatch loudly when running the suite locally
        // but shouldn't break CI on its own. We assert equality so any drift
        // shows up; CI consumers who only care about correctness can run
        // just the first test.
        var dockerfilePath = Path.Combine(RepoRoot, dockerfileRelPath.Replace('/', Path.DirectorySeparatorChar));
        var pinnedRaw = ReadDockerfileArgDefault(dockerfilePath, argName);
        var pinned = SemVer.TryParse(pinnedRaw!);

        if (pinned is null) return; // The other test already fails on parse.
        if (!CliVersionPolicy.KnownGood.TryGetValue(cliKey, out var range)) return;
        if (range.MaxKnown is null) return; // Other test fails this.

        Assert.True(pinned.CompareTo(range.MaxKnown) == 0,
            $"Dockerfile {dockerfileRelPath} pins {pinned} but " +
            $"CliVersionPolicy.KnownGood[\"{cliKey}\"].MaxKnown is {range.MaxKnown}. " +
            $"They should agree on the exact version. Either bump the Dockerfile " +
            $"to match (and rebuild images) or roll back the policy.");
    }

    /// <summary>
    /// Reads the LAST <c>ARG NAME=VALUE</c> assignment for <paramref name="argName"/>
    /// in the Dockerfile. Returns the value or null if not found. Multi-stage
    /// Dockerfiles can re-declare an ARG; "last wins" matches Docker's own
    /// semantics for the build context.
    /// </summary>
    internal static string? ReadDockerfileArgDefault(string dockerfilePath, string argName)
    {
        var content = File.ReadAllText(dockerfilePath);
        var pattern = $@"^\s*ARG\s+{Regex.Escape(argName)}=(?<v>[^\r\n]+)";
        var matches = Regex.Matches(content, pattern, RegexOptions.Multiline);
        if (matches.Count == 0) return null;
        return matches[matches.Count - 1].Groups["v"].Value.Trim();
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
                return dir;
            var parent = Directory.GetParent(dir);
            if (parent is null) break;
            dir = parent.FullName;
        }
        throw new InvalidOperationException(
            $"Could not locate repo root (no .git found above {AppContext.BaseDirectory})");
    }
}
