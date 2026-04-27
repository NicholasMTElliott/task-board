namespace TaskBoard.Worker.Tests.Helpers;

/// <summary>
/// Loads versioned CLI stdout fixtures from
/// <c>Fixtures/Cli/{cli}/{version}/{filename}</c> in the test output directory.
/// The csproj copies the fixture tree on build (see TaskBoard.Worker.Tests.csproj
/// ItemGroup for "Fixtures\Cli\**\*.*").
///
/// <para>
/// Capture workflow for new CLI versions: see <c>docs/CliVersionTesting.md</c>.
/// </para>
/// </summary>
internal static class CliFixtureLoader
{
    /// <summary>Resolves the absolute path to a fixture file.</summary>
    public static string GetFixturePath(string cli, string version, string filename)
        => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Cli", cli, version, filename);

    /// <summary>Reads a fixture file's full text. Throws with a clear message if missing.</summary>
    public static string Read(string cli, string version, string filename)
    {
        var path = GetFixturePath(cli, version, filename);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"CLI fixture not found at {path}. " +
                $"To add a new fixture, see docs/CliVersionTesting.md.",
                path);
        return File.ReadAllText(path);
    }

    /// <summary>
    /// Enumerates every fixture file under <c>Fixtures/Cli/{cli}/</c>, yielding
    /// (version, filename) tuples. Used by [Theory] / MemberData generators in
    /// the parser-corpus tests so adding a new fixture file automatically adds
    /// a new test case without code changes.
    /// </summary>
    public static IEnumerable<(string Version, string Filename)> EnumerateFixtures(string cli)
    {
        var cliDir = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Cli", cli);
        if (!Directory.Exists(cliDir)) yield break;

        foreach (var versionDir in Directory.GetDirectories(cliDir).OrderBy(d => d))
        {
            var version = Path.GetFileName(versionDir);
            foreach (var file in Directory.GetFiles(versionDir).OrderBy(f => f))
                yield return (version, Path.GetFileName(file));
        }
    }
}

/// <summary>
/// Skip-by-default gating for live CLI smoke tests. The tests invoke real CLI
/// subprocesses against real LLM providers — they cost money, take time, and
/// require credentials, so they only run when explicitly enabled.
///
/// <para>
/// Enable for one run with <c>AIBOARD_TEST_LIVE_CLI=1 dotnet test</c>. Run before
/// tagging a release or after upgrading any CLI to confirm the version is still
/// supported end-to-end.
/// </para>
/// </summary>
internal static class CliLiveTestGate
{
    public static bool Enabled =>
        Environment.GetEnvironmentVariable("AIBOARD_TEST_LIVE_CLI") == "1";
}
