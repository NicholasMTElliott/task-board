using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Validation;

/// <summary>
/// Runtime version check for installed CLIs against
/// <see cref="CliVersionPolicy.KnownGood"/>. Called from <c>Program.cs</c> at
/// startup, after the CLI executable is resolved but before the host runs.
///
/// <para>
/// Severity rules:
///   <list type="bullet">
///     <item><see cref="CliVersionStatus.Old"/> → log Error and return
///       <c>false</c>. Startup aborts; the operator must upgrade the CLI before
///       running again. We deliberately fail hard rather than letting an
///       unsupported version produce confusing parser errors mid-card.</item>
///     <item><see cref="CliVersionStatus.Newer"/> → log Warning loudly and
///       return <c>true</c>. Parser may still work; the operator is asked to
///       capture a fixture and bump the policy when convenient.</item>
///     <item><see cref="CliVersionStatus.Supported"/> → log Info and return
///       <c>true</c>.</item>
///     <item><see cref="CliVersionStatus.Unknown"/> → log Info (no policy
///       pinned, or version string didn't parse). Returns <c>true</c>; we never
///       block startup on a missing policy entry.</item>
///   </list>
/// </para>
/// </summary>
public static class CliVersionStartupCheck
{
    /// <summary>
    /// Probes the CLI's <c>--version</c>, logs the resolved value, applies the
    /// policy, and returns <c>true</c> if startup should proceed. Never throws;
    /// a probe failure returns <c>true</c> with a Warning, on the principle
    /// that an unrecorded version is worse than no version check at all.
    /// </summary>
    public static async Task<bool> CheckAsync(
        string cliKey,
        string executablePath,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        // Both CLI resolvers expose the same TryGetVersionAsync surface but
        // they're separate static classes — pick the right one for the key.
        var versionString = cliKey switch
        {
            CliKey.Codex  => await CodexCliResolver.TryGetVersionAsync(executablePath, cancellationToken),
            CliKey.Claude => await ClaudeCliResolver.TryGetVersionAsync(executablePath, cancellationToken),
            _ => "(unknown: no version probe registered for CLI key '" + cliKey + "')",
        };

        if (versionString.StartsWith("(unknown:", StringComparison.Ordinal))
        {
            logger.LogWarning(
                "{CliKey} CLI version probe failed: {VersionString}. " +
                "Executor runs will still be attempted, but CLI-version-drift bugs " +
                "will be harder to diagnose without a recorded version. " +
                "Run '{Path} --version' manually to investigate.",
                cliKey, versionString, executablePath);
            return true;
        }

        var result = CliVersionPolicy.Check(cliKey, versionString);

        switch (result.Status)
        {
            case CliVersionStatus.Old:
                logger.LogError(
                    "{CliKey} CLI version check FAILED: {Message} " +
                    "Raw version string: '{RawVersion}'. Resolved path: '{Path}'.",
                    cliKey, result.Message, versionString, executablePath);
                return false;

            case CliVersionStatus.Newer:
                logger.LogWarning(
                    "{CliKey} CLI version check WARNING: {Message} " +
                    "Raw version string: '{RawVersion}'.",
                    cliKey, result.Message, versionString);
                return true;

            case CliVersionStatus.Supported:
                logger.LogInformation(
                    "{CliKey} CLI version check OK: {Message}",
                    cliKey, result.Message);
                return true;

            case CliVersionStatus.Unknown:
            default:
                logger.LogInformation(
                    "{CliKey} CLI version: {RawVersion} ({PolicyMessage})",
                    cliKey, versionString, result.Message);
                return true;
        }
    }
}
