namespace TaskBoard.Worker.Processing;

/// <summary>
/// Installs bundled Claude Code skills from <c>{exeDir}/skills/&lt;name&gt;/</c>
/// into <c>~/.claude/skills/&lt;name&gt;/</c> (user-scope — available in every
/// Claude Code conversation on this machine).
///
/// <para>
/// Triggered by the <c>--install</c> CLI flag. Runs before the host is built,
/// so it has no DI / config dependencies beyond a logger and (optional) seams.
/// Standalone invocation (<c>aiboard --install</c>) installs and exits;
/// combined invocation (<c>aiboard --install --mode polling</c>) installs and
/// then falls through to the requested mode.
/// </para>
///
/// <para>
/// Per-file overwrite (idempotent re-run upgrades the skill in-place). Does NOT
/// delete existing destination files that aren't in the source bundle —
/// operator-managed customisations survive a re-install. Operators wanting a
/// clean slate should <c>rm -rf ~/.claude/skills/&lt;name&gt;</c> before
/// re-installing.
/// </para>
///
/// <para>
/// "Skill" = any subdirectory of <c>{exeDir}/skills/</c> that contains a
/// <c>SKILL.md</c>. Top-level files (e.g. <c>skills/README.md</c>) are
/// deliberately ignored. Currently only <c>aiboard/</c> ships, but the
/// enumeration is future-proof.
/// </para>
/// </summary>
public sealed class InstallRunner
{
    private readonly ILogger logger;
    private readonly TextWriter stdout;
    private readonly Func<string> getExeDir;
    private readonly Func<string> getUserHome;

    public InstallRunner(
        ILogger logger,
        TextWriter? stdout = null,
        Func<string>? getExeDir = null,
        Func<string>? getUserHome = null)
    {
        this.logger = logger;
        this.stdout = stdout ?? Console.Out;
        this.getExeDir = getExeDir ?? (() => AppContext.BaseDirectory);
        this.getUserHome = getUserHome ?? DefaultGetUserHome;
    }

    /// <summary>
    /// Resolves the user's home directory cross-platform:
    /// Windows → <c>%USERPROFILE%</c>, Linux/macOS → <c>$HOME</c>.
    /// </summary>
    private static string DefaultGetUserHome() =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var exeDir = getExeDir();
        var userHome = getUserHome();

        if (string.IsNullOrEmpty(userHome))
        {
            logger.LogError(
                "Could not resolve user home directory. Set USERPROFILE (Windows) " +
                "or HOME (Linux/macOS) and retry.");
            return Task.FromResult(1);
        }

        var sourceSkillsDir = Path.Combine(exeDir, "skills");
        if (!Directory.Exists(sourceSkillsDir))
        {
            logger.LogError(
                "Bundled skills directory not found at {Path}. " +
                "The aiboard install may be incomplete — re-extract the release archive.",
                sourceSkillsDir);
            return Task.FromResult(1);
        }

        // A "skill" is any subdirectory containing a SKILL.md. Top-level files
        // (skills/README.md, etc.) are not skills and must not be copied.
        var skillDirs = Directory.EnumerateDirectories(sourceSkillsDir)
            .Where(d => File.Exists(Path.Combine(d, "SKILL.md")))
            .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (skillDirs.Count == 0)
        {
            logger.LogWarning(
                "No skills found under {Path} (no subdirectory contained a SKILL.md). " +
                "Nothing to install.",
                sourceSkillsDir);
            return Task.FromResult(0);
        }

        var destSkillsRoot = Path.Combine(userHome, ".claude", "skills");
        try
        {
            Directory.CreateDirectory(destSkillsRoot);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Failed to create destination directory {Path}. " +
                "Check permissions on the user's home directory.",
                destSkillsRoot);
            return Task.FromResult(1);
        }

        var totalFiles = 0;
        foreach (var src in skillDirs)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var skillName = Path.GetFileName(src);
            var dest = Path.Combine(destSkillsRoot, skillName);
            int fileCount;
            try
            {
                fileCount = CopyDirectoryRecursive(src, dest);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Failed to install skill '{Skill}' to {Dest}.",
                    skillName, dest);
                return Task.FromResult(1);
            }

            totalFiles += fileCount;
            stdout.WriteLine($"Installed skill '{skillName}': {fileCount} file(s) → {dest}");
        }

        stdout.WriteLine();
        stdout.WriteLine(
            $"Installed {skillDirs.Count} skill(s), {totalFiles} file(s) total. " +
            $"Reload Claude Code to pick up the changes.");
        return Task.FromResult(0);
    }

    /// <summary>
    /// Copies every file under <paramref name="src"/> to <paramref name="dest"/>,
    /// preserving subdirectory structure. Existing files at the destination are
    /// overwritten. Returns the number of files copied.
    /// </summary>
    private static int CopyDirectoryRecursive(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        var count = 0;
        foreach (var srcFile in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, srcFile);
            var destFile = Path.Combine(dest, rel);
            var destDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);
            File.Copy(srcFile, destFile, overwrite: true);
            count++;
        }
        return count;
    }
}
