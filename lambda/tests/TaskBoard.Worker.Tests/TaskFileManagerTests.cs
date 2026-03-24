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

        await _manager.WriteAllTaskFilesAsync(_tempDir, cards, config, CancellationToken.None);

        Assert.True(File.Exists(TaskFileManager.GetTaskFilePath(_tempDir, "card1")));
        Assert.True(File.Exists(TaskFileManager.GetTaskFilePath(_tempDir, "card2")));
    }

    [Fact]
    public async Task WriteAllTaskFilesAsync_FileContainsFrontmatterAndDescription()
    {
        var cards = new List<BoardCard>
        {
            new("card1", "Auth Middleware", "Build JWT auth layer", "list-design"),
        };
        var config = BuildWorkflowConfig();

        await _manager.WriteAllTaskFilesAsync(_tempDir, cards, config, CancellationToken.None);

        var content = await File.ReadAllTextAsync(TaskFileManager.GetTaskFilePath(_tempDir, "card1"));
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

        await _manager.WriteAllTaskFilesAsync(_tempDir, cards, config, CancellationToken.None);

        var content = await File.ReadAllTextAsync(TaskFileManager.GetTaskFilePath(_tempDir, "card1"));
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

        await _manager.WriteAllTaskFilesAsync(_tempDir, cards, config, CancellationToken.None);
        var content = await _manager.ReadTaskFileAsync(_tempDir, "card1", CancellationToken.None);

        Assert.Contains("Some content here", content);
        Assert.Contains("id: card1", content);
    }

    [Fact]
    public async Task ReadTaskFileAsync_NonexistentCard_Throws()
    {
        // File.ReadAllTextAsync throws FileNotFoundException on Windows,
        // but may throw DirectoryNotFoundException if the .aiboard/tasks dir doesn't exist
        await Assert.ThrowsAnyAsync<IOException>(
            () => _manager.ReadTaskFileAsync(_tempDir, "nonexistent", CancellationToken.None));
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
    public void GetTaskFilePath_ReturnsCorrectPath()
    {
        var path = TaskFileManager.GetTaskFilePath("/repo", "card123");

        Assert.Contains("card123.md", path);
        Assert.Contains(".aiboard", path);
        Assert.Contains("tasks", path);
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
