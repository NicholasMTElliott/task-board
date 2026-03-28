using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests;

public class TaskFileManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly TaskFileManager _manager;

    public TaskFileManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "taskfilemanager-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _manager = new TaskFileManager(NullLogger<TaskFileManager>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task WriteAllTaskFilesAsync_CreatesOneFilePerCard()
    {
        var cards = new List<BoardCard>
        {
            new("card1", "First Card", "Description one", "list-design"),
            new("card2", "Second Card", "Description two", "list-review"),
        };
        var config = BuildWorkflowConfig();

        await _manager.WriteAllTaskFilesAsync(_tempDir, cards, config, cancellationToken: CancellationToken.None);

        Assert.True(File.Exists(TaskFileManager.GetTaskFilePath(_tempDir, "card1", "First Card")));
        Assert.True(File.Exists(TaskFileManager.GetTaskFilePath(_tempDir, "card2", "Second Card")));
    }

    [Fact]
    public async Task WriteAllTaskFilesAsync_FileContainsFrontmatterAndDescription()
    {
        var cards = new List<BoardCard>
        {
            new("card1", "Auth Middleware", "Build JWT auth layer", "list-design"),
        };
        var config = BuildWorkflowConfig();

        await _manager.WriteAllTaskFilesAsync(_tempDir, cards, config, cancellationToken: CancellationToken.None);

        var content = await File.ReadAllTextAsync(TaskFileManager.GetTaskFilePath(_tempDir, "card1", "Auth Middleware"));
        Assert.Contains("id: card1", content);
        Assert.Contains("title: Auth Middleware", content);
        Assert.Contains("list: Design", content);
        Assert.Contains("list_id: list-design", content);
        Assert.Contains("Build JWT auth layer", content);
    }

    [Fact]
    public async Task WriteAllTaskFilesAsync_UnknownListId_ShowsUnknown()
    {
        var cards = new List<BoardCard>
        {
            new("card1", "Card", "Desc", "unknown-list-id"),
        };
        var config = BuildWorkflowConfig();

        await _manager.WriteAllTaskFilesAsync(_tempDir, cards, config, cancellationToken: CancellationToken.None);

        var content = await File.ReadAllTextAsync(TaskFileManager.GetTaskFilePath(_tempDir, "card1", "Card"));
        Assert.Contains("list: Unknown", content);
    }

    [Fact]
    public async Task ReadTaskFileAsync_ReturnsWrittenContent()
    {
        var cards = new List<BoardCard>
        {
            new("card1", "Test Card", "Some content here", "list-design"),
        };
        var config = BuildWorkflowConfig();

        await _manager.WriteAllTaskFilesAsync(_tempDir, cards, config, cancellationToken: CancellationToken.None);
        var content = await _manager.ReadTaskFileAsync(_tempDir, "card1", "Test Card", CancellationToken.None);

        Assert.Contains("Some content here", content);
        Assert.Contains("id: card1", content);
    }

    [Fact]
    public async Task ReadTaskFileAsync_NonexistentCard_Throws()
    {
        // File.ReadAllTextAsync throws FileNotFoundException on Windows,
        // but may throw DirectoryNotFoundException if the .aiboard/tasks dir doesn't exist
        await Assert.ThrowsAnyAsync<IOException>(
            () => _manager.ReadTaskFileAsync(_tempDir, "nonexistent", cancellationToken: CancellationToken.None));
    }

    [Fact]
    public void BuildTaskFileContent_CorrectFormat()
    {
        var card = new BoardCard("abc123", "My Task", "Task description body", "list-id-1");

        var content = TaskFileManager.BuildTaskFileContent(card, "Design");

        Assert.StartsWith("---", content);
        Assert.Contains("id: abc123", content);
        Assert.Contains("title: My Task", content);
        Assert.Contains("list: Design", content);
        Assert.Contains("list_id: list-id-1", content);
        Assert.Contains("Task description body", content);
        // Verify frontmatter is closed and description follows
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("---", lines[0]);
        Assert.Equal("---", lines[5]);
        Assert.Equal("", lines[6]);
        Assert.Equal("Task description body", lines[7]);
    }

    [Fact]
    public void BuildTaskFileContent_EmptyDescription_StillValid()
    {
        var card = new BoardCard("abc", "Title", "", "list-1");

        var content = TaskFileManager.BuildTaskFileContent(card, "Backlog");

        // Frontmatter closed with --- followed by blank line
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("---", lines[5]);
        Assert.Equal("", lines[6]);
    }

    [Fact]
    public void EscapeYamlValue_PlainText_NoQuotes()
    {
        Assert.Equal("Hello World", TaskFileManager.EscapeYamlValue("Hello World"));
    }

    [Fact]
    public void EscapeYamlValue_ContainsColon_Quoted()
    {
        Assert.Equal("\"key: value\"", TaskFileManager.EscapeYamlValue("key: value"));
    }

    [Fact]
    public void EscapeYamlValue_ContainsQuotes_Escaped()
    {
        Assert.Equal("\"say \\\"hello\\\"\"", TaskFileManager.EscapeYamlValue("say \"hello\""));
    }

    [Fact]
    public void EscapeYamlValue_Empty_ReturnsQuotedEmpty()
    {
        Assert.Equal("\"\"", TaskFileManager.EscapeYamlValue(""));
    }

    [Fact]
    public void GetTaskFilePath_WithTitle_ReturnsSluggedPath()
    {
        var path = TaskFileManager.GetTaskFilePath("/repo", "card123", "My Cool Feature");

        Assert.Contains("card123-my-cool-feature.md", path);
        Assert.Contains(".aiboard", path);
        Assert.Contains("tasks", path);
    }

    [Fact]
    public void GetTaskFilePath_WithoutTitle_ReturnsBareIdPath()
    {
        var path = TaskFileManager.GetTaskFilePath("/repo", "card123");

        Assert.Contains("card123.md", path);
        Assert.Contains(".aiboard", path);
        Assert.Contains("tasks", path);
    }

    [Fact]
    public void ExtractBodyFromTaskFile_WithDetailsBlocks_PreservesDetails()
    {
        var taskFile =
            "---\n" +
            "id: card1\n" +
            "title: Test Task\n" +
            "list: Design\n" +
            "list_id: list-design\n" +
            "---\n\n" +
            "# Technical Design\n" +
            "<details><summary>Click to expand full technical design</summary>\n\n" +
            "The detailed content here.\n\n" +
            "</details>";

        var body = TaskFileManager.ExtractBodyFromTaskFile(taskFile);

        Assert.Contains("<details>", body);
        Assert.Contains("<summary>Click to expand full technical design</summary>", body);
        Assert.Contains("The detailed content here.", body);
        Assert.Contains("</details>", body);
        Assert.DoesNotContain("---\nid:", body);
    }

    [Fact]
    public void StripAnnotations_WithDetailsBlocks_PreservesDetails()
    {
        var body =
            "# Design Review Summary\n" +
            "See also #5 ( see .aiboard/tasks/5-some-ticket.md )\n\n" +
            "# Technical Design\n" +
            "<details><summary>Click to expand full technical design</summary>\n\n" +
            "References ticket #5 ( see .aiboard/tasks/5-some-ticket.md ) for context.\n\n" +
            "</details>";

        var result = TaskFileManager.StripAnnotations(body);

        Assert.Contains("<details>", result);
        Assert.Contains("</details>", result);
        Assert.DoesNotContain("( see .aiboard/tasks/5-some-ticket.md )", result);
    }

    [Fact]
    public void FullDesignOutput_RoundTrips_ThroughTaskFileAndParsing()
    {
        // Construct task file in the new format: summary + collapsed reference sections
        var card = new BoardCard(
            "card42",
            "Sample Feature",
            "# Design Review Summary\n" +
            "## Approach\nPrompt-only change to the design step.\n\n" +
            "## Key Decisions\n1. Inline summary vs separate step — chose inline.\n\n" +
            "## Assumptions\n- The agent can follow output-format instructions.\n\n" +
            "## Scope\n- **In scope:** Modify design prompt.\n- **Out of scope:** Orchestrator changes.\n\n" +
            "## Risk\n- Low: Agent may deviate from format; gate check still validates requirements.\n\n" +
            "## Questions for Reviewer\nNone.\n\n" +
            "---\n\n" +
            "# Technical Design\n" +
            "<details><summary>Click to expand full technical design</summary>\n\n" +
            "Detailed technical content.\n\n" +
            "</details>\n\n" +
            "# Decisions\n" +
            "<details><summary>Click to expand decision log</summary>\n\n" +
            "Decision log content.\n\n" +
            "</details>\n\n" +
            "# Implementation\n" +
            "<details><summary>Click to expand implementation breakdown</summary>\n\n" +
            "Step-by-step implementation.\n\n" +
            "</details>",
            "list-design");

        // Build task file → extract body → strip annotations → parse sections
        var taskFileContent = TaskFileManager.BuildTaskFileContent(card, "Design");
        var body = TaskFileManager.ExtractBodyFromTaskFile(taskFileContent);
        var stripped = TaskFileManager.StripAnnotations(body);
        var sections = CardDescriptionParser.ParseSections(stripped);

        // All sections present
        Assert.True(sections.ContainsKey("Design Review Summary"), "Missing Design Review Summary section");
        Assert.True(sections.ContainsKey("Technical Design"), "Missing Technical Design section");
        Assert.True(sections.ContainsKey("Decisions"), "Missing Decisions section");
        Assert.True(sections.ContainsKey("Implementation"), "Missing Implementation section");

        // Summary section is readable (no <details> wrapping)
        Assert.Contains("## Approach", sections["Design Review Summary"]);
        Assert.DoesNotContain("<details>", sections["Design Review Summary"]);

        // Reference sections contain <details> content intact
        Assert.Contains("Detailed technical content.", sections["Technical Design"]);
        Assert.Contains("Decision log content.", sections["Decisions"]);
        Assert.Contains("Step-by-step implementation.", sections["Implementation"]);
    }

    // --- Comment classification and file structure tests ---

    [Theory]
    [InlineData("<!-- agent-step:create_design -->some content", true)]
    [InlineData("<!-- agent-step:optional:security_audit -->content", true)]
    [InlineData("<!-- agent-run:abc123 -->content", true)]
    [InlineData("<!-- gate-check:Ready for Design -->content", true)]
    [InlineData("<!-- merge-run:xyz789 -->content", true)]
    [InlineData("Just a regular human comment", false)]
    [InlineData("<!-- some-other-marker -->content", false)]
    [InlineData("", false)]
    public void ContainsAgentMarker_ClassifiesCorrectly(string body, bool expected)
    {
        Assert.Equal(expected, TaskFileManager.ContainsAgentMarker(body));
    }

    [Fact]
    public async Task WriteCommentsFileAsync_SeparatesHumanAndAgentComments()
    {
        var comments = new List<CardComment>
        {
            new("user1", "<!-- agent-step:design -->**senior_engineer in Designing:**\n\nAgent output",
                DateTimeOffset.Parse("2026-01-01T09:00:00Z")),
            new("user1", "Please use the new API endpoint instead.",
                DateTimeOffset.Parse("2026-01-01T10:00:00Z")),
            new("user1", "<!-- agent-step:implement -->**implementer in Implementing:**\n\nCode written",
                DateTimeOffset.Parse("2026-01-01T11:00:00Z")),
        };

        await _manager.WriteCommentsFileAsync(_tempDir, "card1", "Test", comments, CancellationToken.None);

        var content = await File.ReadAllTextAsync(
            TaskFileManager.GetCommentsFilePath(_tempDir, "card1", "Test"));

        // Human section appears before agent section
        var reviewerIndex = content.IndexOf("# Reviewer Directives", StringComparison.Ordinal);
        var agentIndex = content.IndexOf("# Agent History", StringComparison.Ordinal);
        Assert.True(reviewerIndex >= 0, "Missing Reviewer Directives section");
        Assert.True(agentIndex >= 0, "Missing Agent History section");
        Assert.True(reviewerIndex < agentIndex, "Reviewer Directives must precede Agent History");

        // Human comment content is in the file
        Assert.Contains("Please use the new API endpoint instead.", content);

        // Agent markers are stripped
        Assert.DoesNotContain("<!-- agent-step:", content);
    }

    [Fact]
    public async Task WriteCommentsFileAsync_ReviewerDirectivesSection_IncludesAuthorityNote()
    {
        var comments = new List<CardComment>
        {
            new("user1", "Human feedback here", DateTimeOffset.Parse("2026-01-01T10:00:00Z")),
        };

        await _manager.WriteCommentsFileAsync(_tempDir, "card1", "Test", comments, CancellationToken.None);

        var content = await File.ReadAllTextAsync(
            TaskFileManager.GetCommentsFilePath(_tempDir, "card1", "Test"));

        Assert.Contains("_These comments are from the human project operator and are authoritative._", content);
    }

    [Fact]
    public async Task WriteCommentsFileAsync_OnlyHumanComments_NoAgentSection()
    {
        var comments = new List<CardComment>
        {
            new("user1", "Human feedback here", DateTimeOffset.Parse("2026-01-01T10:00:00Z")),
        };

        await _manager.WriteCommentsFileAsync(_tempDir, "card1", "Test", comments, CancellationToken.None);

        var content = await File.ReadAllTextAsync(
            TaskFileManager.GetCommentsFilePath(_tempDir, "card1", "Test"));

        Assert.Contains("# Reviewer Directives", content);
        Assert.DoesNotContain("# Agent History", content);
    }

    [Fact]
    public async Task WriteCommentsFileAsync_OnlyAgentComments_NoReviewerSection()
    {
        var comments = new List<CardComment>
        {
            new("user1", "<!-- agent-step:design -->Agent output only",
                DateTimeOffset.Parse("2026-01-01T09:00:00Z")),
        };

        await _manager.WriteCommentsFileAsync(_tempDir, "card1", "Test", comments, CancellationToken.None);

        var content = await File.ReadAllTextAsync(
            TaskFileManager.GetCommentsFilePath(_tempDir, "card1", "Test"));

        Assert.DoesNotContain("# Reviewer Directives", content);
        Assert.Contains("# Agent History", content);
    }

    [Fact]
    public async Task WriteCommentsFileAsync_PreservesChronologicalOrderWithinSections()
    {
        var comments = new List<CardComment>
        {
            new("user1", "First human comment", DateTimeOffset.Parse("2026-01-01T08:00:00Z")),
            new("user1", "<!-- agent-step:step1 -->Agent step 1", DateTimeOffset.Parse("2026-01-01T09:00:00Z")),
            new("user1", "Second human comment", DateTimeOffset.Parse("2026-01-01T10:00:00Z")),
            new("user1", "<!-- agent-step:step2 -->Agent step 2", DateTimeOffset.Parse("2026-01-01T11:00:00Z")),
        };

        await _manager.WriteCommentsFileAsync(_tempDir, "card1", "Test", comments, CancellationToken.None);

        var content = await File.ReadAllTextAsync(
            TaskFileManager.GetCommentsFilePath(_tempDir, "card1", "Test"));

        // Within Reviewer Directives, first human before second human
        var first = content.IndexOf("First human comment", StringComparison.Ordinal);
        var second = content.IndexOf("Second human comment", StringComparison.Ordinal);
        Assert.True(first < second, "Human comments should be in chronological order");

        // Within Agent History, step1 before step2
        var step1 = content.IndexOf("Agent step 1", StringComparison.Ordinal);
        var step2 = content.IndexOf("Agent step 2", StringComparison.Ordinal);
        Assert.True(step1 < step2, "Agent comments should be in chronological order");
    }

    [Fact]
    public async Task WriteCommentsFileAsync_EmptyAfterFilter_NoFileWritten()
    {
        var comments = new List<CardComment>
        {
            new("user1", "<!-- agent-step:x -->", DateTimeOffset.Parse("2026-01-01T09:00:00Z")),
        };

        await _manager.WriteCommentsFileAsync(_tempDir, "card1", "Test", comments, CancellationToken.None);

        Assert.False(File.Exists(TaskFileManager.GetCommentsFilePath(_tempDir, "card1", "Test")));
    }

    [Fact]
    public async Task WriteCommentsFileAsync_AgentComment_StripsConversationLog()
    {
        var agentBody = "<!-- agent-step:design -->**senior_engineer in Designing:**\n\n" +
            "## Agent Complete\n\nDesign output here.\n\n" +
            "<details>\n<summary>Agent conversation log</summary>\n\n" +
            "Let me read the file...\n---\nAnalyzed and wrote the design.\n\n</details>";

        var comments = new List<CardComment>
        {
            new("user1", agentBody, DateTimeOffset.Parse("2026-01-01T09:00:00Z")),
        };

        await _manager.WriteCommentsFileAsync(_tempDir, "card1", "Test", comments, CancellationToken.None);

        var content = await File.ReadAllTextAsync(
            TaskFileManager.GetCommentsFilePath(_tempDir, "card1", "Test"));

        Assert.Contains("Design output here.", content);
        Assert.DoesNotContain("Agent conversation log", content);
        Assert.DoesNotContain("Let me read the file...", content);
        Assert.DoesNotContain("<details>", content);
    }

    private static WorkflowConfig BuildWorkflowConfig()
    {
        return new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["list-design"] = new("Design", "senior_engineer", "agent_run", "Do design work",
                    new Dictionary<string, string> { ["COMPLETE"] = "list-review" }),
                ["list-review"] = new("Design Review", null, "manual_gate", null,
                    new Dictionary<string, string>()),
            },
            Roles: new Dictionary<string, WorkflowRole>
            {
                ["senior_engineer"] = new("opus-4.6", "You are a Senior Engineer.", new List<string> { "Technical Design", "Decisions" }),
            });
    }
}
