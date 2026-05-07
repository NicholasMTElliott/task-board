namespace TaskBoard.Worker.Validation;

/// <summary>
/// Single source of truth for CLI version ranges we've validated against.
/// Consumed by both startup version-check (loud warnings for new versions, errors
/// for old ones) and by the parser-corpus / live-smoke test suite (asserting the
/// running CLI is in the supported range).
///
/// <para>
/// Update workflow when a new CLI version drops:
///   1. Capture a sample of its <c>--json</c> stdout into
///      <c>lambda/tests/TaskBoard.Worker.Tests/Fixtures/Cli/{cli}/{version}/</c>.
///   2. Run the parser-corpus tests; ensure they pass against the new shape.
///      If they don't, fix the parser FIRST and add a regression-pinning fixture
///      under the new version directory.
///   3. Bump <see cref="KnownGood"/>'s <c>MaxKnown</c> for that CLI to the new version.
///   4. If the new version drops support for an old wire shape (Codex 0.125.0
///      dropped top-level structured_output events), bump <c>MinSupported</c>
///      so operators on older versions are blocked at startup with a clear error.
/// </para>
///
/// <para>See <c>docs/CliVersionTesting.md</c> for the full workflow.</para>
/// </summary>
public static class CliVersionPolicy
{
    /// <summary>
    /// Known-good ranges per CLI key. Keys match the names used by
    /// <see cref="CliKey"/> constants. <c>MinSupported</c> = oldest version we
    /// guarantee the parser handles; older = error at startup.
    /// <c>MaxKnown</c> = newest version we've captured a fixture for; newer =
    /// warning at startup ("you're on uncharted ground, please report back").
    /// </summary>
    public static readonly IReadOnlyDictionary<string, CliVersionRange> KnownGood =
        new Dictionary<string, CliVersionRange>(StringComparer.OrdinalIgnoreCase)
        {
            // Codex CLI 0.125.0 dropped top-level structured_output events in favour
            // of the agent_message item.completed shape. Older versions use a parser
            // path we no longer maintain test fixtures for; mark them unsupported.
            // 0.128.0 is the version baked into docker/codex-sandbox/Dockerfile and
            // tested in production; we worked around its installation_id O_RDWR|O_CREAT
            // behaviour via the CODEX_INSTALLATION_ID env var path.
            // DockerfilePolicyDriftTests asserts MaxKnown >= the Dockerfile pin.
            [CliKey.Codex] = new(MinSupported: new SemVer(0, 125, 0), MaxKnown: new SemVer(0, 128, 0)),

            // Claude CLI 2.1.126 is the version baked into docker/agent-sandbox/Dockerfile.
            // 2.x is the era where ~/.claude.json (file at home, sibling of .claude/)
            // became a hard requirement — see DockerClaudeMountBuilder's claude.json
            // mount. MinSupported left null until we capture fixtures from earlier
            // versions we want to keep working with.
            [CliKey.Claude] = new(MinSupported: null, MaxKnown: new SemVer(2, 1, 126)),

            // OpenCode CLI 1.14.26 is the version baked into docker/opencode-sandbox/Dockerfile.
            // OpenCode runs only inside the sandbox image, so this check effectively
            // pins the image. Operators upgrade by rebuilding the image — the runtime
            // check fires from inside the container at first use.
            [CliKey.OpenCode] = new(MinSupported: null, MaxKnown: new SemVer(1, 14, 26)),
        };

    /// <summary>
    /// Classifies a CLI version against the known-good range for its key.
    /// Never throws — returns <see cref="CliVersionStatus.Unknown"/> on any
    /// parse failure or missing-policy entry so the caller can decide whether
    /// to proceed (we never block startup on a bad version-string format).
    /// </summary>
    public static CliVersionCheckResult Check(string cliKey, string rawVersionString)
    {
        if (!KnownGood.TryGetValue(cliKey, out var range))
            return new CliVersionCheckResult(CliVersionStatus.Unknown, null,
                $"No version policy registered for CLI '{cliKey}'.");

        var parsed = SemVer.TryParse(rawVersionString);
        if (parsed is null)
            return new CliVersionCheckResult(CliVersionStatus.Unknown, null,
                $"Could not parse a SemVer from CLI version string: '{rawVersionString}'.");

        if (range.MinSupported is null && range.MaxKnown is null)
            return new CliVersionCheckResult(CliVersionStatus.Unknown, parsed,
                $"No version range pinned for '{cliKey}' yet — running version {parsed} without policy enforcement.");

        if (range.MinSupported is not null && parsed.CompareTo(range.MinSupported) < 0)
            return new CliVersionCheckResult(CliVersionStatus.Old, parsed,
                $"{cliKey} CLI version {parsed} is below the minimum supported version {range.MinSupported}. " +
                $"Older versions emit a wire shape this build no longer parses; upgrade the CLI before continuing.");

        if (range.MaxKnown is not null && parsed.CompareTo(range.MaxKnown) > 0)
            return new CliVersionCheckResult(CliVersionStatus.Newer, parsed,
                $"{cliKey} CLI version {parsed} is newer than the highest version pinned in CliVersionPolicy ({range.MaxKnown}). " +
                $"Parsing may still work, but no fixture covers this version. " +
                $"If runs succeed, capture stdout into Fixtures/Cli/{cliKey}/{parsed}/ and bump CliVersionPolicy.MaxKnown. " +
                $"If runs fail, report the failure and downgrade the CLI in the meantime.");

        return new CliVersionCheckResult(CliVersionStatus.Supported, parsed,
            $"{cliKey} CLI version {parsed} is within the supported range " +
            $"[{range.MinSupported?.ToString() ?? "any"} .. {range.MaxKnown?.ToString() ?? "any"}].");
    }
}

/// <summary>Stable string keys for CLI policy entries. Matches the CLI executable name.</summary>
public static class CliKey
{
    public const string Codex = "codex";
    public const string Claude = "claude";
    public const string OpenCode = "opencode";
}

public sealed record CliVersionRange(SemVer? MinSupported, SemVer? MaxKnown);

public enum CliVersionStatus
{
    /// <summary>No policy entry, no parseable version, or policy explicitly unpinned.</summary>
    Unknown,
    /// <summary>Below MinSupported. Hard error at startup — known to break.</summary>
    Old,
    /// <summary>Within [MinSupported, MaxKnown]. Proceed silently.</summary>
    Supported,
    /// <summary>Above MaxKnown. Warning at startup — proceed but loudly.</summary>
    Newer,
}

public sealed record CliVersionCheckResult(
    CliVersionStatus Status,
    SemVer? ParsedVersion,
    string Message);

/// <summary>
/// Minimal SemVer-ish parser. Extracts the first <c>major.minor.patch</c>
/// triple from a string (handles "codex-cli 0.125.0", "1.0.34 (Claude Code)",
/// "v0.5.2", etc.). Pre-release / build-metadata suffixes are ignored — the
/// orderable triple is what matters for the policy check.
/// </summary>
public sealed record SemVer(int Major, int Minor, int Patch) : IComparable<SemVer>
{
    private static readonly System.Text.RegularExpressions.Regex Pattern =
        new(@"(\d+)\.(\d+)\.(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static SemVer? TryParse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var match = Pattern.Match(input);
        if (!match.Success) return null;
        return new SemVer(
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            int.Parse(match.Groups[3].Value));
    }

    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        var c = Major.CompareTo(other.Major);
        if (c != 0) return c;
        c = Minor.CompareTo(other.Minor);
        if (c != 0) return c;
        return Patch.CompareTo(other.Patch);
    }

    public override string ToString() => $"{Major}.{Minor}.{Patch}";
}
