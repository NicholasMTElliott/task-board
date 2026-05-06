using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Pins <see cref="InstallRunner"/> behaviour: skill discovery, recursive copy,
/// idempotent overwrite, and per-edge-case exit codes.
/// </summary>
public class InstallRunnerTests : IDisposable
{
    private readonly string _scratch;
    private readonly string _exeDir;
    private readonly string _userHome;
    private readonly StringBuilder _stdoutSb = new();
    private readonly StringWriter _stdout;

    public InstallRunnerTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(),
            "install-runner-" + Guid.NewGuid().ToString("N")[..8]);
        _exeDir = Path.Combine(_scratch, "exe");
        _userHome = Path.Combine(_scratch, "home");
        Directory.CreateDirectory(_exeDir);
        Directory.CreateDirectory(_userHome);
        _stdout = new StringWriter(_stdoutSb);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    private InstallRunner BuildRunner() => new(
        NullLogger.Instance,
        stdout: _stdout,
        getExeDir: () => _exeDir,
        getUserHome: () => _userHome);

    private void AuthorSkill(string skillName, params (string RelPath, string Content)[] files)
    {
        var dir = Path.Combine(_exeDir, "skills", skillName);
        Directory.CreateDirectory(dir);
        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
    }

    private string DestSkillPath(string skillName) =>
        Path.Combine(_userHome, ".claude", "skills", skillName);

    [Fact]
    public async Task InstallsSingleSkillToUserHome()
    {
        AuthorSkill("aiboard",
            ("SKILL.md", "---\nname: aiboard\n---\nbody"),
            ("README.md", "skill readme"));

        var exit = await BuildRunner().RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        var dest = DestSkillPath("aiboard");
        Assert.True(File.Exists(Path.Combine(dest, "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(dest, "README.md")));
        Assert.Equal("---\nname: aiboard\n---\nbody",
            File.ReadAllText(Path.Combine(dest, "SKILL.md")));
        Assert.Contains("Installed skill 'aiboard'", _stdoutSb.ToString());
        Assert.Contains("2 file(s)", _stdoutSb.ToString());
    }

    [Fact]
    public async Task PreservesNestedSubdirectoryStructure()
    {
        AuthorSkill("aiboard",
            ("SKILL.md", "x"),
            ("scripts/helper.sh", "echo hi"),
            ("docs/refs/notes.md", "# notes"));

        var exit = await BuildRunner().RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        var dest = DestSkillPath("aiboard");
        Assert.True(File.Exists(Path.Combine(dest, "scripts", "helper.sh")));
        Assert.True(File.Exists(Path.Combine(dest, "docs", "refs", "notes.md")));
    }

    [Fact]
    public async Task IdempotentReinstallOverwritesExistingFiles()
    {
        AuthorSkill("aiboard", ("SKILL.md", "v1"));
        var first = await BuildRunner().RunAsync(CancellationToken.None);
        Assert.Equal(0, first);
        Assert.Equal("v1", File.ReadAllText(Path.Combine(DestSkillPath("aiboard"), "SKILL.md")));

        // Author a new version of the skill — re-run should overwrite.
        File.WriteAllText(Path.Combine(_exeDir, "skills", "aiboard", "SKILL.md"), "v2");
        var second = await BuildRunner().RunAsync(CancellationToken.None);

        Assert.Equal(0, second);
        Assert.Equal("v2", File.ReadAllText(Path.Combine(DestSkillPath("aiboard"), "SKILL.md")));
    }

    [Fact]
    public async Task PreservesOperatorAddedFilesAtDestination()
    {
        // Operators may add their own files into ~/.claude/skills/aiboard/ between
        // installs (notes, customisations). The runner must not delete them.
        AuthorSkill("aiboard", ("SKILL.md", "x"));
        await BuildRunner().RunAsync(CancellationToken.None);

        var operatorFile = Path.Combine(DestSkillPath("aiboard"), "my-notes.md");
        File.WriteAllText(operatorFile, "operator data");

        var second = await BuildRunner().RunAsync(CancellationToken.None);

        Assert.Equal(0, second);
        Assert.True(File.Exists(operatorFile));
        Assert.Equal("operator data", File.ReadAllText(operatorFile));
    }

    [Fact]
    public async Task InstallsMultipleSkillsInOneRun()
    {
        AuthorSkill("aiboard", ("SKILL.md", "a"));
        AuthorSkill("other-skill", ("SKILL.md", "b"));

        var exit = await BuildRunner().RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(DestSkillPath("aiboard"), "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(DestSkillPath("other-skill"), "SKILL.md")));
        var output = _stdoutSb.ToString();
        Assert.Contains("Installed skill 'aiboard'", output);
        Assert.Contains("Installed skill 'other-skill'", output);
        Assert.Contains("Installed 2 skill(s)", output);
    }

    [Fact]
    public async Task SkipsSubdirectoriesWithoutSkillMd()
    {
        // Top-level skills/README.md is correctly ignored (it's not a directory).
        // A subdirectory without SKILL.md is also a non-skill (e.g. operator
        // dropped a stray dir into the bundle).
        AuthorSkill("aiboard", ("SKILL.md", "real"));
        Directory.CreateDirectory(Path.Combine(_exeDir, "skills", "not-a-skill"));
        File.WriteAllText(Path.Combine(_exeDir, "skills", "not-a-skill", "stuff.txt"), "x");
        File.WriteAllText(Path.Combine(_exeDir, "skills", "README.md"), "skills readme");

        var exit = await BuildRunner().RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(DestSkillPath("aiboard"), "SKILL.md")));
        Assert.False(Directory.Exists(DestSkillPath("not-a-skill")));
        Assert.False(File.Exists(Path.Combine(_userHome, ".claude", "skills", "README.md")));
    }

    [Fact]
    public async Task NoSkillsBundled_LogsWarningAndReturnsZero()
    {
        // Empty skills/ directory — nothing to install but not an error.
        Directory.CreateDirectory(Path.Combine(_exeDir, "skills"));

        var exit = await BuildRunner().RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.False(Directory.Exists(Path.Combine(_userHome, ".claude", "skills")));
    }

    [Fact]
    public async Task SkillsDirectoryMissing_ReturnsOne()
    {
        // No skills/ directory under exeDir — install is incomplete, error.
        var exit = await BuildRunner().RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task EmptyUserHome_ReturnsOne()
    {
        AuthorSkill("aiboard", ("SKILL.md", "x"));

        var runner = new InstallRunner(
            NullLogger.Instance,
            stdout: _stdout,
            getExeDir: () => _exeDir,
            getUserHome: () => "");

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
    }

    [Fact]
    public async Task CreatesClaudeSkillsDirIfMissing()
    {
        // Fresh user home — ~/.claude/ doesn't exist yet.
        AuthorSkill("aiboard", ("SKILL.md", "x"));
        Assert.False(Directory.Exists(Path.Combine(_userHome, ".claude")));

        var exit = await BuildRunner().RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.True(Directory.Exists(Path.Combine(_userHome, ".claude", "skills")));
        Assert.True(File.Exists(Path.Combine(DestSkillPath("aiboard"), "SKILL.md")));
    }

    [Fact]
    public async Task OutputContainsReloadReminder()
    {
        AuthorSkill("aiboard", ("SKILL.md", "x"));

        var exit = await BuildRunner().RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("Reload Claude Code", _stdoutSb.ToString());
    }

    [Fact]
    public async Task SkillsAreInstalledInDeterministicAlphaOrder()
    {
        // Multiple skills enumerate in OrdinalIgnoreCase order so the user-facing
        // log lines are stable across runs (and across OS-dependent FS enumeration
        // orders). Pinned because operators may grep the install output.
        AuthorSkill("zebra", ("SKILL.md", "z"));
        AuthorSkill("alpha", ("SKILL.md", "a"));
        AuthorSkill("middle", ("SKILL.md", "m"));

        var exit = await BuildRunner().RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        var output = _stdoutSb.ToString();
        var alphaIdx = output.IndexOf("'alpha'", StringComparison.Ordinal);
        var middleIdx = output.IndexOf("'middle'", StringComparison.Ordinal);
        var zebraIdx = output.IndexOf("'zebra'", StringComparison.Ordinal);
        Assert.True(alphaIdx >= 0 && middleIdx >= 0 && zebraIdx >= 0);
        Assert.True(alphaIdx < middleIdx, "alpha must precede middle");
        Assert.True(middleIdx < zebraIdx, "middle must precede zebra");
    }
}
