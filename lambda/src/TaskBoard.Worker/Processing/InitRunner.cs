using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Scaffolds <c>./.aiboard/</c> for a new project from a bundled template.
///
/// <para>
/// Pulls a template directory (under <c>{exeDir}/templates/&lt;name&gt;/</c>)
/// containing a <c>workflow.json</c> + <c>appsettings.json</c> pair, copies it
/// into <c>./.aiboard/</c>, and substitutes <c>__OWNER__</c> /
/// <c>__OWNER_SLASH_REPO__</c> / <c>__PROJECT_NUMBER__</c> placeholders with
/// values from configuration (CLI flags, env, or interactive prompts).
/// </para>
///
/// <para>
/// Designed to run BEFORE the main host is built: needs only IConfiguration
/// for input and a logger for output. Uses neither WorkflowConfig nor
/// ITaskBoardClient — those services are not registered yet at the point this
/// runs.
/// </para>
/// </summary>
public sealed class InitRunner
{
    /// <summary>
    /// Subcommand for the external-command delegate. Returns
    /// (exitCode, stdout, stderr).
    /// </summary>
    public delegate Task<(int ExitCode, string Stdout, string Stderr)> ExternalCommandDelegate(
        string command, IReadOnlyList<string> args, CancellationToken cancellationToken);

    public static readonly IReadOnlyList<string> KnownTemplates = new[]
    {
        "from-scratch-claude",
        "from-scratch-codex",
        "from-scratch-opencode",
    };

    private const string DefaultTemplate = "from-scratch-claude";

    private readonly IConfiguration config;
    private readonly ILogger logger;
    private readonly TextReader stdin;
    private readonly TextWriter stdout;
    private readonly Func<string> getCwd;
    private readonly Func<string> getExeDir;
    private readonly ExternalCommandDelegate? runExternal;

    public InitRunner(
        IConfiguration config,
        ILogger logger,
        TextReader? stdin = null,
        TextWriter? stdout = null,
        Func<string>? getCwd = null,
        Func<string>? getExeDir = null,
        ExternalCommandDelegate? runExternal = null)
    {
        this.config = config;
        this.logger = logger;
        this.stdin = stdin ?? Console.In;
        this.stdout = stdout ?? Console.Out;
        this.getCwd = getCwd ?? Directory.GetCurrentDirectory;
        this.getExeDir = getExeDir ?? (() => AppContext.BaseDirectory);
        this.runExternal = runExternal;
    }

    /// <summary>
    /// One-shot entry point used by Program.cs.
    /// </summary>
    public static Task<int> RunAsync(
        IConfiguration config, ILogger logger, CancellationToken cancellationToken)
        => new InitRunner(config, logger).RunAsync(cancellationToken);

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var templateName = (config["Init:Template"] ?? DefaultTemplate).Trim();
        var force = ParseBool(config["Init:Force"]);
        var nonInteractive = ParseBool(config["Init:NonInteractive"]);

        if (!KnownTemplates.Any(t => string.Equals(t, templateName, StringComparison.OrdinalIgnoreCase)))
        {
            logger.LogError(
                "Unknown template '{Template}'. Available: {Available}",
                templateName, string.Join(", ", KnownTemplates));
            return 1;
        }

        var templateDir = ResolveTemplateDir(templateName);
        if (templateDir is null)
        {
            logger.LogError(
                "Template directory not found for '{Template}'. " +
                "Expected at: {Expected}. " +
                "If you're running from source, ensure templates/ is copied to the build output.",
                templateName,
                Path.Combine(getExeDir(), "templates", templateName));
            return 1;
        }

        var cwd = getCwd();
        var targetDir = Path.Combine(cwd, ".aiboard");
        if (Directory.Exists(targetDir) && !force)
        {
            logger.LogError(
                "./.aiboard/ already exists at {Path}. Pass --force to overwrite, " +
                "or remove the directory first.", targetDir);
            return 1;
        }

        var (owner, ownerSlashRepo, projectNumber) =
            await ResolveSubstitutionsAsync(nonInteractive, cancellationToken);

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(owner)) missing.Add("--github-owner");
        if (string.IsNullOrWhiteSpace(ownerSlashRepo)) missing.Add("--github-repo");
        if (string.IsNullOrWhiteSpace(projectNumber)) missing.Add("--github-project");
        if (missing.Count > 0)
        {
            logger.LogError(
                "Missing required value(s) for init: {Missing}. " +
                "Provide via flags, env (GitHubProjects__Owner etc.), or run interactively.",
                string.Join(", ", missing));
            return 1;
        }

        // Defensive: if the user typed just "owner" for the repo, fold it into owner/owner.
        if (!ownerSlashRepo!.Contains('/'))
            ownerSlashRepo = $"{owner}/{ownerSlashRepo}";

        var substitutions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["__OWNER__"] = owner!,
            ["__OWNER_SLASH_REPO__"] = ownerSlashRepo,
            ["__PROJECT_NUMBER__"] = projectNumber!,
        };

        Directory.CreateDirectory(targetDir);
        await CopyWithSubstitutionAsync(templateDir, targetDir, substitutions, cancellationToken);

        // Sanity check: appsettings.json must parse as JSON post-substitution.
        var appsettingsPath = Path.Combine(targetDir, "appsettings.json");
        try
        {
            using var stream = File.OpenRead(appsettingsPath);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Generated appsettings.json failed JSON validation at {Path}. " +
                "This indicates a substitution or template bug.", appsettingsPath);
            return 1;
        }

        PrintNextSteps(templateName, owner!, ownerSlashRepo, projectNumber!);
        return 0;
    }

    /// <summary>Probes the templates root next to the executable.</summary>
    private string? ResolveTemplateDir(string templateName)
    {
        var probed = new[]
        {
            Path.Combine(getExeDir(), "templates", templateName),
            // dev fallback: when running via `dotnet run` from the worker directory
            Path.Combine(getExeDir(), "..", "..", "..", "..", "..", "..", "templates", templateName),
        };
        return Array.Find(probed, Directory.Exists);
    }

    private async Task<(string? Owner, string? OwnerSlashRepo, string? ProjectNumber)>
        ResolveSubstitutionsAsync(bool nonInteractive, CancellationToken cancellationToken)
    {
        var owner = config["GitHubProjects:Owner"];
        var ownerSlashRepo = config["GitHubProjects:Repo"];
        var projectNumber = config["GitHubProjects:ProjectNumber"];

        if (!nonInteractive)
        {
            // Try `gh repo view` to fill owner/repo automatically.
            if ((string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(ownerSlashRepo))
                && runExternal is not null)
            {
                try
                {
                    var (exit, stdoutText, _) = await runExternal(
                        "gh", new[] { "repo", "view", "--json", "owner,name" },
                        cancellationToken);
                    if (exit == 0 && !string.IsNullOrWhiteSpace(stdoutText))
                    {
                        using var doc = JsonDocument.Parse(stdoutText);
                        var ownerLogin = doc.RootElement.GetProperty("owner").GetProperty("login").GetString();
                        var repoName = doc.RootElement.GetProperty("name").GetString();
                        if (!string.IsNullOrWhiteSpace(ownerLogin) && !string.IsNullOrWhiteSpace(repoName))
                        {
                            owner ??= ownerLogin;
                            ownerSlashRepo ??= $"{ownerLogin}/{repoName}";
                            stdout.WriteLine($"Detected GitHub repo via `gh`: {ownerLogin}/{repoName}");
                        }
                    }
                }
                catch
                {
                    // Best effort; gh may not be installed or authed.
                }
            }

            owner = PromptIfMissing("GitHub owner (user or org)", owner);
            ownerSlashRepo = PromptIfMissing("GitHub repo (owner/repo)", ownerSlashRepo);
            projectNumber = PromptIfMissing("GitHub project number", projectNumber);
        }

        return (owner, ownerSlashRepo, projectNumber);
    }

    private string? PromptIfMissing(string label, string? current)
    {
        if (!string.IsNullOrWhiteSpace(current)) return current;
        stdout.Write($"{label}: ");
        stdout.Flush();
        var line = stdin.ReadLine();
        return string.IsNullOrWhiteSpace(line) ? null : line.Trim();
    }

    private async Task CopyWithSubstitutionAsync(
        string sourceDir, string targetDir,
        IReadOnlyDictionary<string, string> substitutions,
        CancellationToken cancellationToken)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rel = Path.GetRelativePath(sourceDir, sourcePath);
            var targetPath = Path.Combine(targetDir, rel);

            var targetParent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetParent))
                Directory.CreateDirectory(targetParent);

            var content = await File.ReadAllTextAsync(sourcePath, cancellationToken);
            foreach (var (key, value) in substitutions)
                content = content.Replace(key, value, StringComparison.Ordinal);

            await File.WriteAllTextAsync(targetPath, content, cancellationToken);
            logger.LogInformation("Wrote {RelPath}",
                Path.GetRelativePath(getCwd(), targetPath));
        }
    }

    private void PrintNextSteps(
        string templateName, string owner, string ownerSlashRepo, string projectNumber)
    {
        stdout.WriteLine();
        stdout.WriteLine("Done. ./.aiboard/ scaffolded.");
        stdout.WriteLine();
        stdout.WriteLine($"  Template:        {templateName}");
        stdout.WriteLine($"  GitHub project:  {ownerSlashRepo} #{projectNumber} (owner: {owner})");
        stdout.WriteLine();
        stdout.WriteLine("Next steps:");
        stdout.WriteLine("  1. Verify the board shape against the workflow:");
        stdout.WriteLine($"       aiboard --mode validation --board-id {projectNumber}");
        stdout.WriteLine("     Fix any Errors it surfaces (typically: missing project fields or columns).");
        stdout.WriteLine();
        stdout.WriteLine("     This template expects:");
        stdout.WriteLine("       - Columns:  Backlog, Ready, In progress, Questions, Problems, Done");
        stdout.WriteLine("       - Fields:   Activity (single-select: Design, Implementation, Review, Test, Merge),");
        stdout.WriteLine("                   priority (single-select: Urgent, Critical, Important, Desired)");
        stdout.WriteLine("       - Labels:   type:story, type:task");
        stdout.WriteLine();
        stdout.WriteLine("  2. Drop a card into 'Ready' with Activity=Design (no assignee), then start polling:");
        stdout.WriteLine($"       aiboard --mode polling --board-id {projectNumber}");
        stdout.WriteLine();
        stdout.WriteLine("  3. To run on a specific card explicitly:");
        stdout.WriteLine($"       aiboard --mode agent --card-id <N> --board-id {projectNumber}");
        stdout.WriteLine();
    }

    private static bool ParseBool(string? value)
        => value is not null && (
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("1", StringComparison.Ordinal) ||
            value.Equals("yes", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(value));   // bare `--force` flag with no value

    /// <summary>
    /// Default external-command runner using <see cref="Process"/>.
    /// Tests inject a stub instead.
    /// </summary>
    public static async Task<(int, string, string)> RunProcessAsync(
        string command, IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);
        await proc.WaitForExitAsync(cancellationToken);
        var stdoutText = await stdoutTask;
        var stderrText = await stderrTask;
        return (proc.ExitCode, stdoutText, stderrText);
    }
}
