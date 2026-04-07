using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

public class UpdateFileProcessorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITaskBoardClient _boardClient;
    private readonly WorkflowConfig _config;
    private readonly AgentIdentity _identity;
    private readonly UpdateFileProcessor _processor;

    public UpdateFileProcessorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "update-processor-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);

        _boardClient = Substitute.For<ITaskBoardClient>();
        _boardClient.CreateCardAsync(Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult("42"));

        _config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Backlog"] = new("Backlog", null, "manual_entry", null, new()),
            },
            Roles: new(),
            CardTypes: new Dictionary<string, CardTypeDefinition>
            {
                ["task"] = new("Task", "type", []),
                ["story"] = new("User Story", "type", ["task"]),
            });

        _identity = new AgentIdentity("Bot", "TestBot", "machine");
        _processor = new UpdateFileProcessor(_boardClient, _config, _identity,
            NullLogger<UpdateFileProcessor>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // ── ParseNewTicketFile ───────────────────────────────────────────────────

    [Fact]
    public void ParseNewTicketFile_WithFrontMatter_ParsesTitleAndBody()
    {
        var content = "---\ntitle: My Task\n---\n\n## Body content\n";

        var result = UpdateFileProcessor.ParseNewTicketFile(content);

        Assert.NotNull(result);
        Assert.Equal("My Task", result.Title);
        Assert.Contains("Body content", result.Body);
    }

    [Fact]
    public void ParseNewTicketFile_WithAllOptionalFields_ParsesTypeParentColumn()
    {
        var content = "---\ntitle: Fix bug\ntype: bug\nparent: 5\ntargetColumn: Backlog\n---\n\nBody here.";

        var result = UpdateFileProcessor.ParseNewTicketFile(content);

        Assert.NotNull(result);
        Assert.Equal("Fix bug", result.Title);
        Assert.Equal("bug", result.Type);
        Assert.Equal("5", result.Parent);
        Assert.Equal("Backlog", result.TargetColumn);
    }

    [Fact]
    public void ParseNewTicketFile_WithH1Title_ExtractsTitleFromBody()
    {
        var content = "# My Title\n\nSome body text.";

        var result = UpdateFileProcessor.ParseNewTicketFile(content);

        Assert.NotNull(result);
        Assert.Equal("My Title", result.Title);
        Assert.Contains("Some body text", result.Body);
    }

    [Fact]
    public void ParseNewTicketFile_EmptyContent_ReturnsNull()
    {
        Assert.Null(UpdateFileProcessor.ParseNewTicketFile(""));
        Assert.Null(UpdateFileProcessor.ParseNewTicketFile("   "));
    }

    [Fact]
    public void ParseNewTicketFile_FrontMatterWithoutTitle_ReturnsNull()
    {
        var content = "---\ntype: bug\n---\n\nBody with no title.";

        Assert.Null(UpdateFileProcessor.ParseNewTicketFile(content));
    }

    [Fact]
    public void ParseNewTicketFile_MissingOptionalFields_ReturnsNullForEach()
    {
        var content = "---\ntitle: Task A\n---\n\nBody.";

        var result = UpdateFileProcessor.ParseNewTicketFile(content);

        Assert.NotNull(result);
        Assert.Equal("Task A", result.Title);
        Assert.Null(result.Type);
        Assert.Null(result.Parent);
        Assert.Null(result.TargetColumn);
    }

    // ── BuildTypeLabel ───────────────────────────────────────────────────────

    [Fact]
    public void BuildTypeLabel_KnownType_UsesConfigPrefix()
    {
        Assert.Equal("type:task", _processor.BuildTypeLabel("task"));
    }

    [Fact]
    public void BuildTypeLabel_UnknownType_FallsBackToDefaultPrefix()
    {
        Assert.Equal("type:bug", _processor.BuildTypeLabel("bug"));
    }

    [Fact]
    public void BuildTypeLabel_NullType_ReturnsNull()
    {
        Assert.Null(_processor.BuildTypeLabel(null));
    }

    // ── ProcessUpdatesAsync: no files ────────────────────────────────────────

    [Fact]
    public async Task ProcessUpdatesAsync_NoUpdatesDir_ReturnsEmpty()
    {
        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, "1", "step1", [], CancellationToken.None);

        Assert.Equal(0, result.FilesProcessed);
        Assert.Empty(result.CreatedCardIds);
    }

    [Fact]
    public async Task ProcessUpdatesAsync_EmptyUpdatesDir_ReturnsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".aiboard", "updates"));

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, "1", "step1", [], CancellationToken.None);

        Assert.Equal(0, result.FilesProcessed);
        Assert.Empty(result.CreatedCardIds);
    }

    // ── ProcessUpdatesAsync: creation ────────────────────────────────────────

    [Fact]
    public async Task ProcessUpdatesAsync_ValidFile_CreatesCardAndDeletesFile()
    {
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-add-login.md"),
            "---\ntitle: Add Login\n---\n\nImplement login feature.");

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, "10", "step1", [], CancellationToken.None);

        Assert.Equal(1, result.FilesProcessed);
        Assert.Single(result.CreatedCardIds);
        Assert.False(File.Exists(Path.Combine(updatesDir, "new-add-login.md")),
            "Processed file should be deleted");

        await _boardClient.Received(1).CreateCardAsync(
            Arg.Is<CreateCardRequest>(r => r.Title == "Add Login"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUpdatesAsync_GenerationConfig_AppliesTypeParentColumn()
    {
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-task-one.md"),
            "---\ntitle: Task One\n---\n\nDo task one.");

        var genConfig = new GenerationConfig("task", "Backlog", true);

        await _processor.ProcessUpdatesAsync(
            _tempDir, "20", "generate_tasks", [], CancellationToken.None, genConfig);

        await _boardClient.Received(1).CreateCardAsync(
            Arg.Is<CreateCardRequest>(r =>
                r.Title == "Task One"
                && r.CardType == "type:task"
                && r.ParentCardId == "20"
                && r.TargetColumn == "Backlog"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUpdatesAsync_GenerationConfig_OverridesFrontMatterValues()
    {
        var updatesDir = CreateUpdatesDir();
        // Front matter specifies type=bug and parent=99, but generationConfig should override
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-task-x.md"),
            "---\ntitle: Task X\ntype: bug\nparent: 99\ntargetColumn: Error\n---\n\nBody.");

        var genConfig = new GenerationConfig("task", "Backlog", true);

        await _processor.ProcessUpdatesAsync(
            _tempDir, "30", "step", [], CancellationToken.None, genConfig);

        await _boardClient.Received(1).CreateCardAsync(
            Arg.Is<CreateCardRequest>(r =>
                r.CardType == "type:task"       // from generationConfig, not front matter "bug"
                && r.ParentCardId == "30"        // from generationConfig sourceCardId, not "99"
                && r.TargetColumn == "Backlog"), // from generationConfig, not "Error"
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUpdatesAsync_NoGenerationConfig_UsesFrontMatterValues()
    {
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-task-y.md"),
            "---\ntitle: Task Y\ntype: task\nparent: 5\ntargetColumn: Backlog\n---\n\nBody.");

        await _processor.ProcessUpdatesAsync(
            _tempDir, "99", "step", [], CancellationToken.None, generationConfig: null);

        await _boardClient.Received(1).CreateCardAsync(
            Arg.Is<CreateCardRequest>(r =>
                r.CardType == "type:task"
                && r.ParentCardId == "5"
                && r.TargetColumn == "Backlog"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUpdatesAsync_Dedup_SkipsAlreadyCreatedSlug()
    {
        var updatesDir = CreateUpdatesDir();
        var filePath = Path.Combine(updatesDir, "new-dupe.md");
        await File.WriteAllTextAsync(filePath, "---\ntitle: Dupe Task\n---\n\nBody.");

        // Simulate already-created comment with dedup marker
        var existingComments = new List<CardComment>
        {
            new("bot", "<!-- agent-created-ticket:dupe -->\nCreated #77: Dupe Task", DateTimeOffset.UtcNow)
        };

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, "1", "step", existingComments, CancellationToken.None);

        // File deleted but no card created
        Assert.False(File.Exists(filePath));
        await _boardClient.DidNotReceive().CreateCardAsync(
            Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUpdatesAsync_LinkToParentFalse_DoesNotSetParentId()
    {
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-no-link.md"),
            "---\ntitle: No Link\n---\n\nBody.");

        var genConfig = new GenerationConfig("task", "Backlog", LinkToParent: false);

        await _processor.ProcessUpdatesAsync(
            _tempDir, "20", "step", [], CancellationToken.None, genConfig);

        await _boardClient.Received(1).CreateCardAsync(
            Arg.Is<CreateCardRequest>(r => r.ParentCardId == null),
            Arg.Any<CancellationToken>());
    }

    private string CreateUpdatesDir()
    {
        var dir = Path.Combine(_tempDir, ".aiboard", "updates");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
