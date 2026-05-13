using System.Diagnostics;
using Microsoft.Extensions.Options;
using TaskBoard.Worker.Clients;

namespace TaskBoard.Worker.Validation;

/// <summary>
/// Writes scaffold actions to a GitHub Projects v2 board via the <c>gh</c> CLI.
/// Sibling of <see cref="GitHubProjectShapeProbe"/> which reads.
/// </summary>
public sealed class GitHubProjectShapeApplier : IBoardShapeApplier
{
    /// <summary>
    /// Optional shell-out delegate for tests. Defaults to a real
    /// <see cref="Process"/> invocation.
    /// </summary>
    public delegate Task<(int ExitCode, string Stdout, string Stderr)> ProcessRunner(
        string command, IReadOnlyList<string> args, CancellationToken cancellationToken);

    private readonly GitHubProjectsOptions _options;
    private readonly ILogger<GitHubProjectShapeApplier> _logger;
    private readonly ProcessRunner _runner;

    public GitHubProjectShapeApplier(
        IOptions<GitHubProjectsOptions> options,
        ILogger<GitHubProjectShapeApplier> logger,
        ProcessRunner? runner = null)
    {
        _options = options.Value;
        _logger = logger;
        _runner = runner ?? DefaultRunner;
    }

    public bool CanApply => true;

    public async Task<ApplyResult> CreateSingleSelectFieldAsync(
        string boardId, string fieldName,
        IReadOnlyList<string> fieldOptions,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.Owner))
            return new ApplyResult(ApplyOutcome.Failed,
                $"create field '{fieldName}'",
                "GitHubProjects:Owner is not configured");

        if (fieldOptions.Count == 0)
            return new ApplyResult(ApplyOutcome.Failed,
                $"create field '{fieldName}'",
                "single-select fields must have at least one option");

        // gh project field-create <projectNumber> --owner <owner>
        //   --name <name>
        //   --data-type SINGLE_SELECT
        //   --single-select-options "<comma-separated>"
        //
        // Reject up-front if any option contains a comma — gh's parser would
        // split mid-name and silently mangle the option list.
        foreach (var o in fieldOptions)
        {
            if (o.Contains(','))
                return new ApplyResult(ApplyOutcome.Failed,
                    $"create field '{fieldName}'",
                    $"option '{o}' contains a comma; gh's --single-select-options is comma-separated");
        }

        var args = new List<string>
        {
            "project", "field-create", boardId,
            "--owner", _options.Owner,
            "--name", fieldName,
            "--data-type", "SINGLE_SELECT",
            "--single-select-options", string.Join(",", fieldOptions),
        };

        int exit;
        string stdout, stderr;
        try
        {
            (exit, stdout, stderr) = await _runner("gh", args, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // gh missing from PATH (Win32Exception 2 / FileNotFoundException) and
            // any other process-startup failure surface as a clean Failed result
            // instead of an unhandled exception that aborts the whole apply pass.
            return new ApplyResult(ApplyOutcome.Failed,
                $"create field '{fieldName}'",
                $"failed to invoke gh: {ex.Message}");
        }
        var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;

        if (exit == 0)
        {
            _logger.LogInformation("Created field '{Field}' with {Count} option(s).",
                fieldName, fieldOptions.Count);
            return new ApplyResult(ApplyOutcome.Created,
                $"create field '{fieldName}' (options: {string.Join(", ", fieldOptions)})");
        }

        if (LooksLikeAlreadyExists(combined))
            return new ApplyResult(ApplyOutcome.AlreadyExists,
                $"field '{fieldName}' already exists");

        _logger.LogWarning("gh project field-create failed: exit={Exit}, output={Out}", exit, combined);
        return new ApplyResult(ApplyOutcome.Failed,
            $"create field '{fieldName}'", combined.Trim());
    }

    public async Task<ApplyResult> CreateLabelAsync(
        string labelName, CancellationToken cancellationToken)
    {
        var repoSlug = _options.Repo.Contains('/')
            ? _options.Repo
            : $"{_options.Owner}/{_options.Repo}";

        if (string.IsNullOrWhiteSpace(repoSlug) || repoSlug == "/")
            return new ApplyResult(ApplyOutcome.Failed,
                $"create label '{labelName}'",
                "GitHubProjects:Repo is not configured");

        var args = new List<string>
        {
            "label", "create", labelName, "--repo", repoSlug,
        };

        int exit;
        string stdout, stderr;
        try
        {
            (exit, stdout, stderr) = await _runner("gh", args, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ApplyResult(ApplyOutcome.Failed,
                $"create label '{labelName}'",
                $"failed to invoke gh: {ex.Message}");
        }
        var combined = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;

        if (exit == 0)
        {
            _logger.LogInformation("Created label '{Label}' on {Repo}.", labelName, repoSlug);
            return new ApplyResult(ApplyOutcome.Created,
                $"create label '{labelName}' on {repoSlug}");
        }

        if (LooksLikeAlreadyExists(combined))
            return new ApplyResult(ApplyOutcome.AlreadyExists,
                $"label '{labelName}' already exists");

        _logger.LogWarning("gh label create failed: exit={Exit}, output={Out}", exit, combined);
        return new ApplyResult(ApplyOutcome.Failed,
            $"create label '{labelName}'", combined.Trim());
    }

    /// <summary>
    /// gh's already-exists messages aren't standardised across subcommands —
    /// project field-create says "already exists", label create says
    /// "Name has already been taken" inside an HTTP-422 response. Bare
    /// "validation failed" is intentionally NOT matched: it would classify
    /// any validation error (invalid name, bad option, missing flag) as
    /// already-exists and silently swallow real failures.
    /// </summary>
    private static bool LooksLikeAlreadyExists(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return false;
        var lower = output.ToLowerInvariant();
        return lower.Contains("already exists")
            || lower.Contains("name has already been taken");
    }

    private static async Task<(int, string, string)> DefaultRunner(
        string command, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        TaskBoard.Worker.Clients.ProcessRunner.ConfigureUtf8Io(psi);
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var outTask = proc.StandardOutput.ReadToEndAsync(ct);
        var errTask = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, await outTask, await errTask);
    }
}
