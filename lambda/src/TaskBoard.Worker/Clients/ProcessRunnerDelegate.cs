namespace TaskBoard.Worker.Clients;

/// <summary>
/// Delegate shape of <see cref="ProcessRunner.RunProcessAsync"/> — an injectable seam
/// that lets tests replay prerecorded (exitCode, stdout, stderr) without launching
/// a real subprocess. Production code uses the static <see cref="ProcessRunner.RunProcessAsync"/>
/// method group directly; only tests should supply a replacement delegate.
/// </summary>
/// <param name="inactivityTimeoutSeconds">
/// Optional — when set, the process is killed if no stdout/stderr output is observed
/// for this many seconds. Acts as an inner cap on top of the hard
/// <paramref name="timeoutSeconds"/> wall-clock limit. Null = inactivity check disabled.
/// </param>
public delegate Task<(int ExitCode, string Stdout, string Stderr)> ProcessRunnerDelegate(
    string executable,
    string[] argumentList,
    string workingDirectory,
    int timeoutSeconds,
    CancellationToken cancellationToken,
    string? stdinData = null,
    IReadOnlyCollection<string>? envVarsToRemove = null,
    string agentName = "agent",
    int? inactivityTimeoutSeconds = null);
