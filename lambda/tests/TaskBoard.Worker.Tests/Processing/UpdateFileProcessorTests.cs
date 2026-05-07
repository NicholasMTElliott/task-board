using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;
using TaskBoard.Worker.Tests.Helpers;

namespace TaskBoard.Worker.Tests.Processing;

public class UpdateFileProcessorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITaskBoardClient _boardClient;
    private readonly WorkflowConfig _config;
    private readonly AgentIdentity _identity;
    private readonly UpdateFileProcessor _processor;

    private const string SourceCardId = "42";
    private const string StepName = "create_design";

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
        _processor = new UpdateFileProcessor(_boardClient, TestWorkflowConfigProvider.Create(_config), _identity,
            NullLogger<UpdateFileProcessor>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }

    // ── Directory / empty cases ──────────────────────────────────────────────

    [Fact]
    public async Task ProcessUpdates_NoDirectory_ReturnsEmpty()
    {
        var workspacePath = Path.Combine(_tempDir, "no-dir");

        var result = await _processor.ProcessUpdatesAsync(
            workspacePath, SourceCardId, StepName, [], CancellationToken.None);

        Assert.False(result.HasUpdates);
        Assert.Empty(result.CreatedTickets);
        Assert.Empty(result.PostedComments);
        await _boardClient.DidNotReceive().CreateCardAsync(Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUpdates_EmptyDirectory_ReturnsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".aiboard", "updates"));

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, SourceCardId, StepName, [], CancellationToken.None);

        Assert.False(result.HasUpdates);
        await _boardClient.DidNotReceive().CreateCardAsync(Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>());
    }

    // ── New ticket: happy path ───────────────────────────────────────────────

    [Fact]
    public async Task ProcessUpdates_NewTicketFile_CreatesCardAndPostsNotification()
    {
        var updatesDir = CreateUpdatesDir();
        var content = "---\ntitle: Fix the auth race condition\n---\n\nDetailed description of the bug.";
        File.WriteAllText(Path.Combine(updatesDir, "new-fix-auth-race.md"), content);

        _boardClient.CreateCardAsync(Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>())
            .Returns("99");

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, SourceCardId, StepName, [], CancellationToken.None);

        Assert.True(result.HasUpdates);
        Assert.Single(result.CreatedTickets);
        Assert.Equal("99", result.CreatedTickets[0].NewCardId);
        Assert.Equal("Fix the auth race condition", result.CreatedTickets[0].Title);
        Assert.Equal("fix-auth-race", result.CreatedTickets[0].Slug);

        // Card should be created
        await _boardClient.Received(1).CreateCardAsync(
            Arg.Is<CreateCardRequest>(r => r.Title == "Fix the auth race condition"),
            Arg.Any<CancellationToken>());

        // Notification comment on source card — marker only in commentMarker param, not body
        await _boardClient.Received(1).UpsertAgentCommentAsync(
            SourceCardId,
            Arg.Is<string>(s => s.Contains("#99") && !s.Contains("agent-created-ticket:fix-auth-race")),
            Arg.Is<string>(s => s.Contains("agent-created-ticket:fix-auth-race")),
            Arg.Any<CancellationToken>());

        // File should be deleted
        Assert.False(File.Exists(Path.Combine(updatesDir, "new-fix-auth-race.md")));
    }

    [Fact]
    public async Task ProcessUpdates_NewTicketFile_EmptyBody_CreatesWithTitleOnly()
    {
        var updatesDir = CreateUpdatesDir();
        File.WriteAllText(Path.Combine(updatesDir, "new-empty-body.md"), "---\ntitle: Empty Body Ticket\n---\n");

        _boardClient.CreateCardAsync(Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>())
            .Returns("55");

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, SourceCardId, StepName, [], CancellationToken.None);

        Assert.Single(result.CreatedTickets);
        await _boardClient.Received(1).CreateCardAsync(
            Arg.Is<CreateCardRequest>(r => r.Title == "Empty Body Ticket"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void ParseNewTicketFile_DependencyFrontMatter_ParsesBlockedByAndBlocks()
    {
        var parsed = UpdateFileProcessor.ParseNewTicketFile("""
            ---
            title: Add migration runner
            estimate: 2
            blockedBy:
              - create-database
              - "#123"
              - current
            blocks: [follow-up-cleanup, "#456"]
            ---

            Body.
            """);

        Assert.NotNull(parsed);
        Assert.Equal(["create-database", "#123", "current"], parsed!.BlockedBy);
        Assert.Equal(["follow-up-cleanup", "#456"], parsed.Blocks);
    }

    [Fact]
    public async Task ProcessUpdates_NewTicketDependencies_ResolvesSameBatchAndCurrentRefs()
    {
        var updatesDir = CreateUpdatesDir();
        File.WriteAllText(Path.Combine(updatesDir, "new-create-database.md"),
            "---\ntitle: Create database\n---\n\nBody.");
        File.WriteAllText(Path.Combine(updatesDir, "new-add-api.md"),
            "---\ntitle: Add API\nblockedBy:\n  - create-database\nblocks:\n  - current\n---\n\nBody.");

        _boardClient.CreateCardAsync(
                Arg.Is<CreateCardRequest>(r => r.Title == "Create database"),
                Arg.Any<CancellationToken>())
            .Returns("101");
        _boardClient.CreateCardAsync(
                Arg.Is<CreateCardRequest>(r => r.Title == "Add API"),
                Arg.Any<CancellationToken>())
            .Returns("102");

        var dependencyClient = Substitute.For<ICardDependencyClient>();
        var processor = new UpdateFileProcessor(
            _boardClient, TestWorkflowConfigProvider.Create(_config), _identity, NullLogger<UpdateFileProcessor>.Instance, dependencyClient);

        var result = await processor.ProcessUpdatesAsync(
            _tempDir, SourceCardId, StepName, [], CancellationToken.None);

        Assert.Equal(2, result.CreatedTickets.Count);
        await dependencyClient.Received(1).AddBlockedByAsync("102", "101", Arg.Any<CancellationToken>());
        await dependencyClient.Received(1).AddBlockedByAsync(SourceCardId, "102", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUpdates_NewTicketDependencies_SkipsSelfDependencyAndSimpleCycle()
    {
        var updatesDir = CreateUpdatesDir();
        File.WriteAllText(Path.Combine(updatesDir, "new-a.md"),
            "---\ntitle: A\nblockedBy:\n  - a\n  - b\n---\n\nBody.");
        File.WriteAllText(Path.Combine(updatesDir, "new-b.md"),
            "---\ntitle: B\nblockedBy:\n  - a\n---\n\nBody.");

        _boardClient.CreateCardAsync(
                Arg.Is<CreateCardRequest>(r => r.Title == "A"),
                Arg.Any<CancellationToken>())
            .Returns("201");
        _boardClient.CreateCardAsync(
                Arg.Is<CreateCardRequest>(r => r.Title == "B"),
                Arg.Any<CancellationToken>())
            .Returns("202");

        var dependencyClient = Substitute.For<ICardDependencyClient>();
        var processor = new UpdateFileProcessor(
            _boardClient, TestWorkflowConfigProvider.Create(_config), _identity, NullLogger<UpdateFileProcessor>.Instance, dependencyClient);

        await processor.ProcessUpdatesAsync(
            _tempDir, SourceCardId, StepName, [], CancellationToken.None);

        await dependencyClient.DidNotReceive().AddBlockedByAsync("201", "201", Arg.Any<CancellationToken>());
        await dependencyClient.Received(1).AddBlockedByAsync("201", "202", Arg.Any<CancellationToken>());
        await dependencyClient.DidNotReceive().AddBlockedByAsync("202", "201", Arg.Any<CancellationToken>());
    }

    // ── New ticket: deduplication ────────────────────────────────────────────

    [Fact]
    public async Task ProcessUpdates_NewTicketFile_DuplicateSlug_SkipsCreation()
    {
        var updatesDir = CreateUpdatesDir();
        File.WriteAllText(Path.Combine(updatesDir, "new-fix-auth-race.md"),
            "---\ntitle: Fix the auth race condition\n---\n\nBody.");

        // Existing comments contain the marker for this slug
        var existingComments = new List<CardComment>
        {
            new("bot", "<!-- agent-created-ticket:fix-auth-race -->\n**New ticket created:** #77", DateTimeOffset.UtcNow)
        };

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, SourceCardId, StepName, existingComments, CancellationToken.None);

        Assert.False(result.HasUpdates);
        Assert.Empty(result.CreatedTickets);
        await _boardClient.DidNotReceive().CreateCardAsync(Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>());

        // File should still be deleted (dedup cleanup)
        Assert.False(File.Exists(Path.Combine(updatesDir, "new-fix-auth-race.md")));
    }

    // ── New ticket: bad format ───────────────────────────────────────────────

    [Fact]
    public async Task ProcessUpdates_NewTicketFile_MissingTitle_SkipsFile()
    {
        var updatesDir = CreateUpdatesDir();
        var filePath = Path.Combine(updatesDir, "new-no-title.md");
        File.WriteAllText(filePath, "---\nauthor: nobody\n---\n\nNo title here.");

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, SourceCardId, StepName, [], CancellationToken.None);

        Assert.False(result.HasUpdates);
        await _boardClient.DidNotReceive().CreateCardAsync(Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>());

        // File should NOT be deleted when it cannot be parsed
        Assert.True(File.Exists(filePath));
    }

    // ── Cross-card comment: happy path ───────────────────────────────────────

    [Fact]
    public async Task ProcessUpdates_CommentFile_PostsOnTargetCard()
    {
        var updatesDir = CreateUpdatesDir();
        var commentBody = "This design impacts ticket #5 in a significant way.";
        File.WriteAllText(Path.Combine(updatesDir, "5-comment.md"), commentBody);

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, SourceCardId, StepName, [], CancellationToken.None);

        Assert.True(result.HasUpdates);
        Assert.Single(result.PostedComments);
        Assert.Equal("5", result.PostedComments[0].TargetCardId);

        // Comment posted on target card — marker only in commentMarker param, not body
        await _boardClient.Received(1).UpsertAgentCommentAsync(
            "5",
            Arg.Is<string>(s => s.Contains(commentBody) && !s.Contains($"agent-cross-comment:{SourceCardId}:{StepName}")),
            Arg.Is<string>(s => s.Contains($"agent-cross-comment:{SourceCardId}:{StepName}")),
            Arg.Any<CancellationToken>());

        // File deleted
        Assert.False(File.Exists(Path.Combine(updatesDir, "5-comment.md")));
    }

    [Fact]
    public async Task ProcessUpdates_CommentFile_TargetIsSelf_SkipsWithWarning()
    {
        var updatesDir = CreateUpdatesDir();
        var filePath = Path.Combine(updatesDir, $"{SourceCardId}-comment.md");
        File.WriteAllText(filePath, "Self-referencing comment.");

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, SourceCardId, StepName, [], CancellationToken.None);

        Assert.False(result.HasUpdates);
        // No comment posted on any card
        await _boardClient.DidNotReceive().UpsertAgentCommentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        // File deleted
        Assert.False(File.Exists(filePath));
    }

    [Fact]
    public async Task ProcessUpdates_CommentFile_EmptyContent_SkipsWithWarning()
    {
        var updatesDir = CreateUpdatesDir();
        var filePath = Path.Combine(updatesDir, "7-comment.md");
        File.WriteAllText(filePath, "   ");

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, SourceCardId, StepName, [], CancellationToken.None);

        Assert.False(result.HasUpdates);
        await _boardClient.DidNotReceive().UpsertAgentCommentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.False(File.Exists(filePath));
    }

    // ── Unrecognized file ────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessUpdates_UnrecognizedFile_LogsWarningAndSkips()
    {
        var updatesDir = CreateUpdatesDir();
        var filePath = Path.Combine(updatesDir, "random-file.md");
        File.WriteAllText(filePath, "Some content.");

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, SourceCardId, StepName, [], CancellationToken.None);

        Assert.False(result.HasUpdates);
        await _boardClient.DidNotReceive().CreateCardAsync(Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>());
        await _boardClient.DidNotReceive().UpsertAgentCommentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        // Unrecognized files are NOT deleted
        Assert.True(File.Exists(filePath));
    }

    // ── Multiple files ───────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessUpdates_MultipleFiles_ProcessesAll()
    {
        var updatesDir = CreateUpdatesDir();
        File.WriteAllText(Path.Combine(updatesDir, "new-bug-one.md"),
            "---\ntitle: Bug One\n---\n\nDescription.");
        File.WriteAllText(Path.Combine(updatesDir, "new-bug-two.md"),
            "---\ntitle: Bug Two\n---\n\nDescription.");
        File.WriteAllText(Path.Combine(updatesDir, "10-comment.md"),
            "Note for card 10.");

        _boardClient.CreateCardAsync(
                Arg.Is<CreateCardRequest>(r => r.Title == "Bug One"), Arg.Any<CancellationToken>())
            .Returns("201");
        _boardClient.CreateCardAsync(
                Arg.Is<CreateCardRequest>(r => r.Title == "Bug Two"), Arg.Any<CancellationToken>())
            .Returns("202");

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, SourceCardId, StepName, [], CancellationToken.None);

        Assert.True(result.HasUpdates);
        Assert.Equal(2, result.CreatedTickets.Count);
        Assert.Single(result.PostedComments);

        await _boardClient.Received(2).CreateCardAsync(Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>());
        await _boardClient.Received(1).UpsertAgentCommentAsync("10", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Error handling ───────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessUpdates_CreateCardFails_LogsWarningAndContinues()
    {
        var updatesDir = CreateUpdatesDir();
        File.WriteAllText(Path.Combine(updatesDir, "new-failing.md"),
            "---\ntitle: Failing Ticket\n---\n\nBody.");
        File.WriteAllText(Path.Combine(updatesDir, "5-comment.md"),
            "This comment should still be posted.");

        _boardClient.CreateCardAsync(Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("gh CLI failed"));

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, SourceCardId, StepName, [], CancellationToken.None);

        // Comment still processed despite ticket creation failure
        Assert.Single(result.PostedComments);
        await _boardClient.Received(1).UpsertAgentCommentAsync(
            "5", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUpdates_UpsertCommentFails_LogsWarningAndContinues()
    {
        var updatesDir = CreateUpdatesDir();
        File.WriteAllText(Path.Combine(updatesDir, "5-comment.md"), "Will fail.");
        File.WriteAllText(Path.Combine(updatesDir, "new-other.md"),
            "---\ntitle: Other Ticket\n---\n\nBody.");

        _boardClient.UpsertAgentCommentAsync("5", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("board client error"));
        _boardClient.CreateCardAsync(Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>())
            .Returns("300");

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, SourceCardId, StepName, [], CancellationToken.None);

        // Ticket still created despite comment failure
        Assert.Single(result.CreatedTickets);
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

    [Fact]
    public void ParseNewTicketFile_QuotedTitle_StripsQuotes()
    {
        var content = "---\ntitle: \"My Quoted Title\"\n---\n\nBody here.";

        var result = UpdateFileProcessor.ParseNewTicketFile(content);

        Assert.NotNull(result);
        Assert.Equal("My Quoted Title", result.Title);
    }

    [Fact]
    public void ParseNewTicketFile_SingleQuotedTitle_StripsQuotes()
    {
        var content = "---\ntitle: 'Single Quoted'\n---\n\nBody.";

        var result = UpdateFileProcessor.ParseNewTicketFile(content);

        Assert.NotNull(result);
        Assert.Equal("Single Quoted", result.Title);
    }

    [Fact]
    public void ParseNewTicketFile_UnclosedFrontMatter_TreatsAsBodyWithH1()
    {
        // No closing --- but has H1 title — HEAD version handles this case
        var content = "# My Title\n\nBody without closing dashes.";

        var result = UpdateFileProcessor.ParseNewTicketFile(content);

        Assert.NotNull(result);
        Assert.Equal("My Title", result.Title);
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

        Assert.False(result.HasUpdates);
        Assert.Empty(result.CreatedTickets);
    }

    [Fact]
    public async Task ProcessUpdatesAsync_EmptyUpdatesDir_ReturnsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".aiboard", "updates"));

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, "1", "step1", [], CancellationToken.None);

        Assert.False(result.HasUpdates);
        Assert.Empty(result.CreatedTickets);
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

        Assert.True(result.HasUpdates);
        Assert.Single(result.CreatedTickets);
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

    // ── AllowedChildren runtime enforcement ─────────────────────────────────

    [Fact]
    public async Task ProcessUpdatesAsync_GenerationConfig_AllowedChildType_CreatesCard()
    {
        // story.allowedChildren = ["task"], so generating a task from a story is allowed
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-allowed-task.md"),
            "---\ntitle: Allowed Task\n---\n\nDo the work.");

        _boardClient.GetCardAsync("20", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new BoardCard("20", "Parent Story", "", "Backlog",
                Labels: ["type:story"])));

        var genConfig = new GenerationConfig("task", "Backlog", true);

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, "20", "generate_tasks", [], CancellationToken.None, genConfig);

        Assert.True(result.HasUpdates);
        Assert.Single(result.CreatedTickets);
        await _boardClient.Received(1).CreateCardAsync(
            Arg.Is<CreateCardRequest>(r => r.Title == "Allowed Task"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUpdatesAsync_GenerationConfig_DisallowedChildType_SkipsCreation()
    {
        // task.allowedChildren = [] (empty), so generating anything from a task is blocked
        var updatesDir = CreateUpdatesDir();
        var filePath = Path.Combine(updatesDir, "new-disallowed-task.md");
        await File.WriteAllTextAsync(filePath, "---\ntitle: Disallowed Task\n---\n\nBody.");

        _boardClient.GetCardAsync("10", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new BoardCard("10", "Parent Task", "", "Backlog",
                Labels: ["type:task"])));

        var genConfig = new GenerationConfig("task", "Backlog", true);

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, "10", "generate_tasks", [], CancellationToken.None, genConfig);

        Assert.False(result.HasUpdates);
        Assert.Empty(result.CreatedTickets);
        await _boardClient.DidNotReceive().CreateCardAsync(Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>());
        Assert.False(File.Exists(filePath), "File should be deleted when creation is skipped");
    }

    [Fact]
    public async Task ProcessUpdatesAsync_GenerationConfig_ParentHasNoTypeLabel_CreatesCard()
    {
        // Parent card has no type label — enforcement is permissive, proceeds with creation
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-untyped-task.md"),
            "---\ntitle: Untyped Parent Task\n---\n\nBody.");

        _boardClient.GetCardAsync("5", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new BoardCard("5", "Untyped Card", "", "Backlog",
                Labels: [])));  // No type label

        var genConfig = new GenerationConfig("task", "Backlog", true);

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, "5", "generate_tasks", [], CancellationToken.None, genConfig);

        Assert.True(result.HasUpdates);
        await _boardClient.Received(1).CreateCardAsync(Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUpdatesAsync_GenerationConfig_GetCardAsyncFails_CreatesCardAnyway()
    {
        // If fetching parent card fails, enforcement is permissive — creation proceeds
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-fetch-fail.md"),
            "---\ntitle: Fetch Fail Task\n---\n\nBody.");

        _boardClient.GetCardAsync("99", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Network error"));

        var genConfig = new GenerationConfig("task", "Backlog", true);

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, "99", "generate_tasks", [], CancellationToken.None, genConfig);

        Assert.True(result.HasUpdates);
        await _boardClient.Received(1).CreateCardAsync(Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUpdatesAsync_NoGenerationConfig_SkipsAllowedChildrenCheck()
    {
        // Without generationConfig, no allowedChildren check is performed at all
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-ad-hoc.md"),
            "---\ntitle: Ad-Hoc Ticket\ntype: task\n---\n\nBody.");

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, "10", "step", [], CancellationToken.None, generationConfig: null);

        Assert.True(result.HasUpdates);
        await _boardClient.DidNotReceive().GetCardAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _boardClient.Received(1).CreateCardAsync(Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>());
    }

    // ── HasCreatedTicketMarker ───────────────────────────────────────────────

    [Fact]
    public void HasCreatedTicketMarker_MarkerPresent_ReturnsTrue()
    {
        var comments = new List<CardComment>
        {
            new("bot", "<!-- agent-created-ticket:fix-auth-race -->\n**New ticket created:** #42", DateTimeOffset.UtcNow)
        };

        Assert.True(UpdateFileProcessor.HasCreatedTicketMarker(comments, "fix-auth-race"));
    }

    [Fact]
    public void HasCreatedTicketMarker_NoMarker_ReturnsFalse()
    {
        var comments = new List<CardComment>
        {
            new("bot", "Some comment without the marker.", DateTimeOffset.UtcNow)
        };

        Assert.False(UpdateFileProcessor.HasCreatedTicketMarker(comments, "fix-auth-race"));
    }

    [Fact]
    public void HasCreatedTicketMarker_DifferentSlug_ReturnsFalse()
    {
        var comments = new List<CardComment>
        {
            new("bot", "<!-- agent-created-ticket:other-slug -->\nSome other ticket.", DateTimeOffset.UtcNow)
        };

        Assert.False(UpdateFileProcessor.HasCreatedTicketMarker(comments, "fix-auth-race"));
    }

    [Fact]
    public void HasCreatedTicketMarker_EmptyComments_ReturnsFalse()
    {
        Assert.False(UpdateFileProcessor.HasCreatedTicketMarker([], "any-slug"));
    }

    // ── Reference file ───────────────────────────────────────────────────────

    [Fact]
    public async Task ProcessUpdatesAsync_ReferenceFile_ReturnsContent()
    {
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "42-reference.md"),
            "# Detailed Analysis\n\nReference content here.");

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, "42", "create_design", [], CancellationToken.None);

        Assert.Equal("# Detailed Analysis\n\nReference content here.", result.ReferenceContent);
        Assert.False(File.Exists(Path.Combine(updatesDir, "42-reference.md"))); // consumed
    }

    [Fact]
    public async Task ProcessUpdatesAsync_NoReferenceFile_ReturnsNull()
    {
        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, "42", "create_design", [], CancellationToken.None);

        Assert.Null(result.ReferenceContent);
    }

    [Fact]
    public async Task ProcessUpdatesAsync_ReferenceFile_NotCountedAsUpdate()
    {
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "42-reference.md"), "ref content");

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, "42", "step", [], CancellationToken.None);

        Assert.False(result.HasUpdates); // reference content is not a "board update"
        Assert.NotNull(result.ReferenceContent);
    }

    // ── Estimate front matter parsing ─────────────────────────────────────

    [Fact]
    public void ParseNewTicketFile_WithEstimate_ParsesEstimateField()
    {
        var content = "---\ntitle: Task A\nestimate: 4\n---\n\nBody.";
        var parsed = UpdateFileProcessor.ParseNewTicketFile(content);

        Assert.NotNull(parsed);
        Assert.Equal("Task A", parsed!.Title);
        Assert.Equal("4", parsed.Estimate);
    }

    [Fact]
    public void ParseNewTicketFile_WithoutEstimate_EstimateIsNull()
    {
        var content = "---\ntitle: Task B\n---\n\nBody.";
        var parsed = UpdateFileProcessor.ParseNewTicketFile(content);

        Assert.NotNull(parsed);
        Assert.Null(parsed!.Estimate);
    }

    [Fact]
    public void ParseNewTicketFile_WithAllFields_ParsesEverything()
    {
        var content = "---\ntitle: Full Task\ntype: task\nparent: 5\ntargetColumn: Backlog\nestimate: 2\n---\n\nBody.";
        var parsed = UpdateFileProcessor.ParseNewTicketFile(content);

        Assert.NotNull(parsed);
        Assert.Equal("Full Task", parsed!.Title);
        Assert.Equal("task", parsed.Type);
        Assert.Equal("5", parsed.Parent);
        Assert.Equal("Backlog", parsed.TargetColumn);
        Assert.Equal("2", parsed.Estimate);
    }

    // ── Estimate field values on created cards ─────────────────────────────

    [Fact]
    public async Task ProcessUpdatesAsync_EstimateInFrontMatter_SetsFieldValueOnCard()
    {
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-est-task.md"),
            "---\ntitle: Estimated Task\nestimate: 4\n---\n\nBody.");

        await _processor.ProcessUpdatesAsync(
            _tempDir, "10", "step", [], CancellationToken.None);

        await _boardClient.Received(1).CreateCardAsync(
            Arg.Is<CreateCardRequest>(r =>
                r.Title == "Estimated Task"
                && r.FieldValues != null
                && r.FieldValues.ContainsKey("Estimate")
                && r.FieldValues["Estimate"] == "4"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUpdatesAsync_NoEstimate_FieldValuesIsNull()
    {
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-no-est.md"),
            "---\ntitle: No Estimate\n---\n\nBody.");

        await _processor.ProcessUpdatesAsync(
            _tempDir, "10", "step", [], CancellationToken.None);

        await _boardClient.Received(1).CreateCardAsync(
            Arg.Is<CreateCardRequest>(r =>
                r.Title == "No Estimate"
                && r.FieldValues == null),
            Arg.Any<CancellationToken>());
    }

    // ── TotalEstimate aggregation ──────────────────────────────────────────

    [Fact]
    public async Task ProcessUpdatesAsync_MultipleTicketsWithEstimates_SumsTotal()
    {
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-task-a.md"),
            "---\ntitle: Task A\nestimate: 2\n---\n\nBody.");
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-task-b.md"),
            "---\ntitle: Task B\nestimate: 4\n---\n\nBody.");

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, "10", "step", [], CancellationToken.None);

        Assert.Equal(2, result.CreatedTickets.Count);
        Assert.Equal(6.0, result.TotalEstimate);
    }

    [Fact]
    public async Task ProcessUpdatesAsync_NoEstimates_TotalEstimateIsNull()
    {
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-task-c.md"),
            "---\ntitle: Task C\n---\n\nBody.");

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, "10", "step", [], CancellationToken.None);

        Assert.Null(result.TotalEstimate);
    }

    [Fact]
    public async Task ProcessUpdatesAsync_MixedEstimates_SumsOnlyProvided()
    {
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-task-d.md"),
            "---\ntitle: Task D\nestimate: 8\n---\n\nBody.");
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-task-e.md"),
            "---\ntitle: Task E\n---\n\nBody."); // no estimate

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, "10", "step", [], CancellationToken.None);

        Assert.Equal(8.0, result.TotalEstimate);
    }

    // ── CopyFields from parent ─────────────────────────────────────────────

    [Fact]
    public async Task ProcessUpdatesAsync_CopyFields_CopiesPriorityFromParent()
    {
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-copy-task.md"),
            "---\ntitle: Copy Task\n---\n\nBody.");

        _boardClient.GetCardAsync("20", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new BoardCard("20", "Parent Story", "", "Backlog",
                Metadata: new Dictionary<string, string> { ["priority"] = "P0" },
                Labels: ["type:story"])));

        var genConfig = new GenerationConfig("task", "Backlog", true,
            CopyFields: ["priority"]);

        await _processor.ProcessUpdatesAsync(
            _tempDir, "20", "generate_tasks", [], CancellationToken.None, genConfig);

        await _boardClient.Received(1).CreateCardAsync(
            Arg.Is<CreateCardRequest>(r =>
                r.FieldValues != null
                && r.FieldValues.ContainsKey("priority")
                && r.FieldValues["priority"] == "P0"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUpdatesAsync_CopyFields_ParentMissingField_SkipsField()
    {
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-missing-field.md"),
            "---\ntitle: Missing Field Task\n---\n\nBody.");

        _boardClient.GetCardAsync("20", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new BoardCard("20", "Parent", "", "Backlog",
                Metadata: new Dictionary<string, string>(), // no priority
                Labels: ["type:story"])));

        var genConfig = new GenerationConfig("task", "Backlog", true,
            CopyFields: ["priority"]);

        await _processor.ProcessUpdatesAsync(
            _tempDir, "20", "generate_tasks", [], CancellationToken.None, genConfig);

        // Card should be created but without priority in FieldValues
        await _boardClient.Received(1).CreateCardAsync(
            Arg.Is<CreateCardRequest>(r =>
                r.FieldValues == null || !r.FieldValues.ContainsKey("priority")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUpdatesAsync_CopyFieldsAndEstimate_MergesBoth()
    {
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-merged.md"),
            "---\ntitle: Merged Task\nestimate: 2\n---\n\nBody.");

        _boardClient.GetCardAsync("20", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new BoardCard("20", "Parent", "", "Backlog",
                Metadata: new Dictionary<string, string> { ["priority"] = "P1" },
                Labels: ["type:story"])));

        var genConfig = new GenerationConfig("task", "Backlog", true,
            CopyFields: ["priority"]);

        await _processor.ProcessUpdatesAsync(
            _tempDir, "20", "generate_tasks", [], CancellationToken.None, genConfig);

        await _boardClient.Received(1).CreateCardAsync(
            Arg.Is<CreateCardRequest>(r =>
                r.FieldValues != null
                && r.FieldValues["priority"] == "P1"
                && r.FieldValues["Estimate"] == "2"),
            Arg.Any<CancellationToken>());
    }

    // ── Skip notification comments for structured generation ────────────

    [Fact]
    public async Task ProcessUpdatesAsync_GenerationConfig_SkipsNotificationComment()
    {
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-gen-task.md"),
            "---\ntitle: Gen Task\n---\n\nBody.");

        _boardClient.GetCardAsync("20", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new BoardCard("20", "Parent", "", "Backlog",
                Labels: ["type:story"])));

        var genConfig = new GenerationConfig("task", "Backlog", true);

        await _processor.ProcessUpdatesAsync(
            _tempDir, "20", "generate_tasks", [], CancellationToken.None, genConfig);

        // Card created but NO notification comment posted
        await _boardClient.Received(1).CreateCardAsync(
            Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>());
        await _boardClient.DidNotReceive().UpsertAgentCommentAsync(
            "20", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUpdatesAsync_NoGenerationConfig_PostsNotificationComment()
    {
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-adhoc-task.md"),
            "---\ntitle: Ad-Hoc Task\n---\n\nBody.");

        _boardClient.CreateCardAsync(Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>())
            .Returns("77");

        await _processor.ProcessUpdatesAsync(
            _tempDir, "10", "step", [], CancellationToken.None, generationConfig: null);

        // Notification comment IS posted for ad-hoc creation
        await _boardClient.Received(1).UpsertAgentCommentAsync(
            "10", Arg.Any<string>(),
            Arg.Is<string>(s => s.Contains("agent-created-ticket:adhoc-task")),
            Arg.Any<CancellationToken>());
    }

    // ── SetFields on generationConfig ────────────────────────────────────────

    [Fact]
    public async Task ProcessUpdatesAsync_SetFields_AppliesLiteralFieldValues()
    {
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-sf.md"),
            "---\ntitle: SF Task\n---\n\nBody.");

        var genConfig = new GenerationConfig("task", "Backlog", true,
            CopyFields: null,
            SetFields: new Dictionary<string, string>
            {
                ["Type"] = "Task",
                ["Activity"] = "Design",
            });

        await _processor.ProcessUpdatesAsync(
            _tempDir, "20", "generate_tasks", [], CancellationToken.None, genConfig);

        await _boardClient.Received(1).CreateCardAsync(
            Arg.Is<CreateCardRequest>(r =>
                r.FieldValues != null
                && r.FieldValues["Type"] == "Task"
                && r.FieldValues["Activity"] == "Design"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUpdatesAsync_SetFields_WinOverCopyFieldsOnKeyCollision()
    {
        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-collision.md"),
            "---\ntitle: Collision\n---\n\nBody.");

        _boardClient.GetCardAsync("20", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new BoardCard("20", "Parent", "", "Backlog",
                Metadata: new Dictionary<string, string> { ["Activity"] = "Test" })));

        var genConfig = new GenerationConfig("task", "Backlog", true,
            CopyFields: ["Activity"],
            SetFields: new Dictionary<string, string> { ["Activity"] = "Design" });

        await _processor.ProcessUpdatesAsync(
            _tempDir, "20", "generate_tasks", [], CancellationToken.None, genConfig);

        // setFields wins
        await _boardClient.Received(1).CreateCardAsync(
            Arg.Is<CreateCardRequest>(r =>
                r.FieldValues != null && r.FieldValues["Activity"] == "Design"),
            Arg.Any<CancellationToken>());
    }

    // ── Field-based card type discriminator (CardTypeField) ─────────────────

    [Fact]
    public async Task ProcessUpdatesAsync_CardTypeField_WritesTypeNameToField()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Backlog"] = new("Backlog", null, "manual_entry", null, new()),
            },
            Roles: new(),
            CardTypes: new Dictionary<string, CardTypeDefinition>
            {
                ["task"] = new("Task", LabelPrefix: null, AllowedChildren: []),
                ["story"] = new("User Story", LabelPrefix: null, AllowedChildren: ["task"]),
            },
            CardTypeField: "Type");

        var processor = new UpdateFileProcessor(_boardClient, TestWorkflowConfigProvider.Create(config), _identity,
            NullLogger<UpdateFileProcessor>.Instance);

        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-ftask.md"),
            "---\ntitle: Field Task\n---\n\nBody.");

        var genConfig = new GenerationConfig("task", "Backlog", true);

        await processor.ProcessUpdatesAsync(
            _tempDir, "20", "generate_tasks", [], CancellationToken.None, genConfig);

        // Type field set to CardTypeDefinition.Name ("Task"), no type label
        await _boardClient.Received(1).CreateCardAsync(
            Arg.Is<CreateCardRequest>(r =>
                r.CardType == null
                && r.FieldValues != null
                && r.FieldValues["Type"] == "Task"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUpdatesAsync_CardTypeField_ParentTypeResolvedFromField()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Backlog"] = new("Backlog", null, "manual_entry", null, new()),
            },
            Roles: new(),
            CardTypes: new Dictionary<string, CardTypeDefinition>
            {
                // "task" is NOT an allowed child of "story" — should skip creation
                ["task"] = new("Task", LabelPrefix: null, AllowedChildren: []),
                ["story"] = new("User Story", LabelPrefix: null, AllowedChildren: []),
            },
            CardTypeField: "Type");

        var processor = new UpdateFileProcessor(_boardClient, TestWorkflowConfigProvider.Create(config), _identity,
            NullLogger<UpdateFileProcessor>.Instance);

        _boardClient.GetCardAsync("20", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new BoardCard("20", "Parent Story", "", "Backlog",
                Metadata: new Dictionary<string, string> { ["Type"] = "User Story" },
                Labels: []))); // no labels; discrimination is field-based

        var updatesDir = CreateUpdatesDir();
        await File.WriteAllTextAsync(
            Path.Combine(updatesDir, "new-blocked.md"),
            "---\ntitle: Blocked Task\n---\n\nBody.");

        var genConfig = new GenerationConfig("task", "Backlog", true);

        await processor.ProcessUpdatesAsync(
            _tempDir, "20", "generate_tasks", [], CancellationToken.None, genConfig);

        // Parent was identified as "story" via Type field; child type "task" not allowed → no creation
        await _boardClient.DidNotReceive().CreateCardAsync(
            Arg.Any<CreateCardRequest>(), Arg.Any<CancellationToken>());
    }

    // ── Label opt-out (empty LabelPrefix) ───────────────────────────────────

    [Fact]
    public void BuildTypeLabel_EmptyLabelPrefix_ReturnsNull()
    {
        var config = new WorkflowConfig(
            States: new Dictionary<string, WorkflowState>
            {
                ["Backlog"] = new("Backlog", null, "manual_entry", null, new()),
            },
            Roles: new(),
            CardTypes: new Dictionary<string, CardTypeDefinition>
            {
                ["task"] = new("Task", LabelPrefix: null, AllowedChildren: []),
            });

        var processor = new UpdateFileProcessor(_boardClient, TestWorkflowConfigProvider.Create(config), _identity,
            NullLogger<UpdateFileProcessor>.Instance);

        Assert.Null(processor.BuildTypeLabel("task"));
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string CreateUpdatesDir()
    {
        var dir = Path.Combine(_tempDir, ".aiboard", "updates");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
