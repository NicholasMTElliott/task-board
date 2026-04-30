using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Pins <see cref="InitRunner"/> behaviour: template copy, placeholder
/// substitution, idempotency guards, and prompt/non-interactive paths.
/// </summary>
public class InitRunnerTests : IDisposable
{
    private readonly string _scratch;
    private readonly string _exeDir;
    private readonly string _cwd;
    private readonly string _templateDir;

    public InitRunnerTests()
    {
        _scratch = Path.Combine(Path.GetTempPath(),
            "init-runner-" + Guid.NewGuid().ToString("N")[..8]);
        _exeDir = Path.Combine(_scratch, "exe");
        _cwd = Path.Combine(_scratch, "project");
        _templateDir = Path.Combine(_exeDir, "templates");
        Directory.CreateDirectory(_exeDir);
        Directory.CreateDirectory(_cwd);
        Directory.CreateDirectory(_templateDir);

        // Author a tiny synthetic template so tests don't depend on the real
        // bundled ones (they evolve, and unit tests should pin behaviour, not
        // current content).
        var tmpl = Path.Combine(_templateDir, "from-scratch-claude");
        Directory.CreateDirectory(tmpl);
        File.WriteAllText(Path.Combine(tmpl, "appsettings.json"), """
            {
              "BoardProvider": "github",
              "GitHubProjects": {
                "Owner": "__OWNER__",
                "Repo": "__OWNER_SLASH_REPO__",
                "ProjectNumber": "__PROJECT_NUMBER__"
              }
            }
            """);
        File.WriteAllText(Path.Combine(tmpl, "workflow.json"), """{"states":{}}""");
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch { /* best effort */ }
    }

    private InitRunner BuildRunner(
        IDictionary<string, string?>? overrides = null,
        TextReader? stdin = null,
        TextWriter? stdout = null,
        InitRunner.ExternalCommandDelegate? runExternal = null)
    {
        var data = new Dictionary<string, string?>
        {
            ["Init:Template"] = "from-scratch-claude",
            ["Init:NonInteractive"] = "true",
        };
        if (overrides is not null)
            foreach (var (k, v) in overrides) data[k] = v;

        var config = new ConfigurationBuilder().AddInMemoryCollection(data).Build();
        return new InitRunner(
            config, NullLogger.Instance,
            stdin: stdin,
            stdout: stdout,
            getCwd: () => _cwd,
            getExeDir: () => _exeDir,
            runExternal: runExternal);
    }

    [Fact]
    public async Task NonInteractive_HappyPath_WritesAiboardWithSubstitutions()
    {
        var runner = BuildRunner(new Dictionary<string, string?>
        {
            ["GitHubProjects:Owner"] = "acme",
            ["GitHubProjects:Repo"] = "acme/widgets",
            ["GitHubProjects:ProjectNumber"] = "7",
        }, stdout: new StringWriter());

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        var aiboardDir = Path.Combine(_cwd, ".aiboard");
        Assert.True(File.Exists(Path.Combine(aiboardDir, "appsettings.json")));
        Assert.True(File.Exists(Path.Combine(aiboardDir, "workflow.json")));

        var settings = File.ReadAllText(Path.Combine(aiboardDir, "appsettings.json"));
        Assert.Contains("\"Owner\": \"acme\"", settings);
        Assert.Contains("\"Repo\": \"acme/widgets\"", settings);
        Assert.Contains("\"ProjectNumber\": \"7\"", settings);
        Assert.DoesNotContain("__OWNER__", settings);
        Assert.DoesNotContain("__OWNER_SLASH_REPO__", settings);
        Assert.DoesNotContain("__PROJECT_NUMBER__", settings);

        // Generated appsettings.json must be valid JSON.
        using var doc = JsonDocument.Parse(settings);
        var owner = doc.RootElement.GetProperty("GitHubProjects").GetProperty("Owner").GetString();
        Assert.Equal("acme", owner);
    }

    [Fact]
    public async Task NonInteractive_MissingValues_FailsExitOne()
    {
        var runner = BuildRunner(new Dictionary<string, string?>
        {
            ["GitHubProjects:Owner"] = "acme",
            // Repo + ProjectNumber omitted
        }, stdout: new StringWriter());

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(Directory.Exists(Path.Combine(_cwd, ".aiboard")));
    }

    [Fact]
    public async Task RepoWithoutSlash_IsExpandedToOwnerSlashRepo()
    {
        var runner = BuildRunner(new Dictionary<string, string?>
        {
            ["GitHubProjects:Owner"] = "acme",
            ["GitHubProjects:Repo"] = "widgets",   // no slash; common operator typo
            ["GitHubProjects:ProjectNumber"] = "7",
        }, stdout: new StringWriter());

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        var settings = File.ReadAllText(Path.Combine(_cwd, ".aiboard", "appsettings.json"));
        Assert.Contains("\"Repo\": \"acme/widgets\"", settings);
    }

    [Fact]
    public async Task UnknownTemplate_FailsExitOne()
    {
        var runner = BuildRunner(new Dictionary<string, string?>
        {
            ["Init:Template"] = "made-up-template",
            ["GitHubProjects:Owner"] = "acme",
            ["GitHubProjects:Repo"] = "acme/widgets",
            ["GitHubProjects:ProjectNumber"] = "7",
        }, stdout: new StringWriter());

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(Directory.Exists(Path.Combine(_cwd, ".aiboard")));
    }

    [Fact]
    public async Task ExistingAiboardDir_FailsWithoutForce()
    {
        var existing = Path.Combine(_cwd, ".aiboard");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "guard.txt"), "do not delete");

        var runner = BuildRunner(new Dictionary<string, string?>
        {
            ["GitHubProjects:Owner"] = "acme",
            ["GitHubProjects:Repo"] = "acme/widgets",
            ["GitHubProjects:ProjectNumber"] = "7",
        }, stdout: new StringWriter());

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
        // Existing file must still be there — init must not clobber on guard.
        Assert.True(File.Exists(Path.Combine(existing, "guard.txt")));
    }

    [Fact]
    public async Task ExistingAiboardDir_Force_OverwritesContents()
    {
        var existing = Path.Combine(_cwd, ".aiboard");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "appsettings.json"), "old garbage");

        var runner = BuildRunner(new Dictionary<string, string?>
        {
            ["Init:Force"] = "true",
            ["GitHubProjects:Owner"] = "acme",
            ["GitHubProjects:Repo"] = "acme/widgets",
            ["GitHubProjects:ProjectNumber"] = "7",
        }, stdout: new StringWriter());

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        var settings = File.ReadAllText(Path.Combine(existing, "appsettings.json"));
        Assert.DoesNotContain("old garbage", settings);
        Assert.Contains("\"Owner\": \"acme\"", settings);
    }

    [Fact]
    public async Task TemplateMissingFromExeDir_FailsCleanly()
    {
        // Wipe the template we set up so the resolver finds nothing.
        Directory.Delete(_templateDir, recursive: true);

        var runner = BuildRunner(new Dictionary<string, string?>
        {
            ["GitHubProjects:Owner"] = "acme",
            ["GitHubProjects:Repo"] = "acme/widgets",
            ["GitHubProjects:ProjectNumber"] = "7",
        }, stdout: new StringWriter());

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(1, exit);
        Assert.False(Directory.Exists(Path.Combine(_cwd, ".aiboard")));
    }

    [Fact]
    public async Task Interactive_PromptsForMissingValues_UsesStdinAnswers()
    {
        var input = new StringReader("acme\nacme/widgets\n7\n");
        var output = new StringWriter();

        // NOT non-interactive — let prompts fire.
        var runner = BuildRunner(new Dictionary<string, string?>
        {
            ["Init:NonInteractive"] = "false",
        }, stdin: input, stdout: output);

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        var settings = File.ReadAllText(Path.Combine(_cwd, ".aiboard", "appsettings.json"));
        Assert.Contains("\"Owner\": \"acme\"", settings);
        Assert.Contains("\"Repo\": \"acme/widgets\"", settings);
        Assert.Contains("\"ProjectNumber\": \"7\"", settings);

        // Stdout should have prompted for each missing value.
        var promptText = output.ToString();
        Assert.Contains("GitHub owner", promptText);
        Assert.Contains("GitHub repo", promptText);
        Assert.Contains("GitHub project number", promptText);
    }

    [Fact]
    public async Task Interactive_GhAutoDetect_FillsOwnerAndRepoFromShellOutput()
    {
        // gh stub returns owner=acme, name=widgets — only project-number is then prompted.
        InitRunner.ExternalCommandDelegate ghStub = (cmd, args, _) =>
        {
            Assert.Equal("gh", cmd);
            Assert.Contains("repo", args);
            Assert.Contains("view", args);
            return Task.FromResult((0,
                """{ "owner": { "login": "acme" }, "name": "widgets" }""",
                ""));
        };

        var input = new StringReader("7\n");   // only the project-number prompt
        var output = new StringWriter();

        var runner = BuildRunner(new Dictionary<string, string?>
        {
            ["Init:NonInteractive"] = "false",
        }, stdin: input, stdout: output, runExternal: ghStub);

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        var settings = File.ReadAllText(Path.Combine(_cwd, ".aiboard", "appsettings.json"));
        Assert.Contains("\"Owner\": \"acme\"", settings);
        Assert.Contains("\"Repo\": \"acme/widgets\"", settings);
        Assert.Contains("\"ProjectNumber\": \"7\"", settings);
        Assert.Contains("Detected GitHub repo via `gh`", output.ToString());
    }

    [Fact]
    public async Task Interactive_GhFails_FallsBackToPromptingForOwnerAndRepo()
    {
        // gh stub fails; the runner must prompt for everything.
        InitRunner.ExternalCommandDelegate ghStub =
            (_, _, _) => Task.FromResult((128, "", "not a gh repo"));

        var input = new StringReader("acme\nacme/widgets\n7\n");
        var output = new StringWriter();

        var runner = BuildRunner(new Dictionary<string, string?>
        {
            ["Init:NonInteractive"] = "false",
        }, stdin: input, stdout: output, runExternal: ghStub);

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        var settings = File.ReadAllText(Path.Combine(_cwd, ".aiboard", "appsettings.json"));
        Assert.Contains("\"Owner\": \"acme\"", settings);
    }

    [Fact]
    public async Task NonInteractive_EnvVarsViaConfiguration_AreUsed()
    {
        // Caller wired GitHubProjects:* into IConfiguration without flags
        // (e.g. from env). The non-interactive path should take them as-is
        // without prompting.
        var runner = BuildRunner(new Dictionary<string, string?>
        {
            ["GitHubProjects:Owner"] = "from-env",
            ["GitHubProjects:Repo"] = "from-env/repo",
            ["GitHubProjects:ProjectNumber"] = "42",
        }, stdout: new StringWriter());

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        var settings = File.ReadAllText(Path.Combine(_cwd, ".aiboard", "appsettings.json"));
        Assert.Contains("\"Owner\": \"from-env\"", settings);
    }

    [Fact]
    public async Task Force_PreservesOperatorAddedFiles_OverwritesOnlyTemplateFiles()
    {
        // Behaviour pin: --force should be non-destructive to operator additions.
        // A user who hand-edited .aiboard/appsettings.user.json (local secrets)
        // shouldn't lose it when re-running init to update the workflow.
        var aiboard = Path.Combine(_cwd, ".aiboard");
        Directory.CreateDirectory(aiboard);
        File.WriteAllText(Path.Combine(aiboard, "appsettings.json"), "old template content");
        File.WriteAllText(Path.Combine(aiboard, "appsettings.user.json"), "OPERATOR_SECRETS");

        var runner = BuildRunner(new Dictionary<string, string?>
        {
            ["Init:Force"] = "true",
            ["GitHubProjects:Owner"] = "acme",
            ["GitHubProjects:Repo"] = "acme/widgets",
            ["GitHubProjects:ProjectNumber"] = "7",
        }, stdout: new StringWriter());

        var exit = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, exit);
        // Template file was overwritten with the substituted version.
        var settings = File.ReadAllText(Path.Combine(aiboard, "appsettings.json"));
        Assert.DoesNotContain("old template content", settings);
        Assert.Contains("\"Owner\": \"acme\"", settings);
        // Operator-added file untouched.
        Assert.True(File.Exists(Path.Combine(aiboard, "appsettings.user.json")));
        Assert.Equal("OPERATOR_SECRETS", File.ReadAllText(Path.Combine(aiboard, "appsettings.user.json")));
    }

    [Fact]
    public async Task BundledTemplates_AllProduceValidJsonWhenSubstituted()
    {
        // Smoke test the real shipped templates: each one should produce parseable
        // JSON after substitution. This guards against template-author mistakes
        // (unbalanced quotes around a placeholder, missing comma, etc.).
        var bundledRoot = Path.Combine(AppContext.BaseDirectory, "templates");
        if (!Directory.Exists(bundledRoot))
            return; // skip in environments where templates haven't been copied

        foreach (var tmpl in InitRunner.KnownTemplates)
        {
            var src = Path.Combine(bundledRoot, tmpl);
            if (!Directory.Exists(src)) continue;
            var settingsSrc = Path.Combine(src, "appsettings.json");
            var workflowSrc = Path.Combine(src, "workflow.json");
            Assert.True(File.Exists(settingsSrc), $"{tmpl} missing appsettings.json");
            Assert.True(File.Exists(workflowSrc), $"{tmpl} missing workflow.json");

            var settings = File.ReadAllText(settingsSrc)
                .Replace("__OWNER__", "acme")
                .Replace("__OWNER_SLASH_REPO__", "acme/widgets")
                .Replace("__PROJECT_NUMBER__", "4");
            using var settingsDoc = JsonDocument.Parse(settings);
            using var workflowDoc = JsonDocument.Parse(File.ReadAllText(workflowSrc));
        }
    }
}
