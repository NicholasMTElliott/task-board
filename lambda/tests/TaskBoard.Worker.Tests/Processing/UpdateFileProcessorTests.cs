using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

public class UpdateFileProcessorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly ITaskBoardClient _boardClient;
    private readonly UpdateFileProcessor _processor;

    private const string SourceCardId = "42";
    private const string StepName = "create_design";

    public UpdateFileProcessorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ufp-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _boardClient = Substitute.For<ITaskBoardClient>();
        _processor = new UpdateFileProcessor(_boardClient, NullLogger<UpdateFileProcessor>.Instance);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
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
        await _boardClient.DidNotReceive().CreateCardAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProcessUpdates_EmptyDirectory_ReturnsEmpty()
    {
        Directory.CreateDirectory(Path.Combine(_tempDir, ".aiboard", "updates"));

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, SourceCardId, StepName, [], CancellationToken.None);

        Assert.False(result.HasUpdates);
        await _boardClient.DidNotReceive().CreateCardAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── New ticket: happy path ───────────────────────────────────────────────

    [Fact]
    public async Task ProcessUpdates_NewTicketFile_CreatesCardAndPostsNotification()
    {
        var updatesDir = CreateUpdatesDir();
        var content = "---\ntitle: Fix the auth race condition\n---\n\nDetailed description of the bug.";
        File.WriteAllText(Path.Combine(updatesDir, "new-fix-auth-race.md"), content);

        _boardClient.CreateCardAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
            "Fix the auth race condition", Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Notification comment on source card
        await _boardClient.Received(1).UpsertAgentCommentAsync(
            SourceCardId,
            Arg.Is<string>(s => s.Contains("agent-created-ticket:fix-auth-race") && s.Contains("#99")),
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

        _boardClient.CreateCardAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("55");

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, SourceCardId, StepName, [], CancellationToken.None);

        Assert.Single(result.CreatedTickets);
        await _boardClient.Received(1).CreateCardAsync("Empty Body Ticket", Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        await _boardClient.DidNotReceive().CreateCardAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

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
        await _boardClient.DidNotReceive().CreateCardAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

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

        // Comment posted on target card
        await _boardClient.Received(1).UpsertAgentCommentAsync(
            "5",
            Arg.Is<string>(s => s.Contains($"agent-cross-comment:{SourceCardId}:{StepName}") && s.Contains(commentBody)),
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
        await _boardClient.DidNotReceive().CreateCardAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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

        _boardClient.CreateCardAsync("Bug One", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("201");
        _boardClient.CreateCardAsync("Bug Two", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("202");

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, SourceCardId, StepName, [], CancellationToken.None);

        Assert.True(result.HasUpdates);
        Assert.Equal(2, result.CreatedTickets.Count);
        Assert.Single(result.PostedComments);

        await _boardClient.Received(2).CreateCardAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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

        _boardClient.CreateCardAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
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
        _boardClient.CreateCardAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("300");

        var result = await _processor.ProcessUpdatesAsync(
            _tempDir, SourceCardId, StepName, [], CancellationToken.None);

        // Ticket still created despite comment failure
        Assert.Single(result.CreatedTickets);
    }

    // ── ParseNewTicketFile ───────────────────────────────────────────────────

    [Fact]
    public void ParseNewTicketFile_ValidFormat_ExtractsFields()
    {
        var content = "---\ntitle: My Bug Fix\n---\n\nDetailed description of the bug.";

        var result = UpdateFileProcessor.ParseNewTicketFile(content);

        Assert.NotNull(result);
        Assert.Equal("My Bug Fix", result!.Value.Title);
        Assert.Equal("Detailed description of the bug.", result.Value.Body);
    }

    [Fact]
    public void ParseNewTicketFile_QuotedTitle_StripsQuotes()
    {
        var content = "---\ntitle: \"My Quoted Title\"\n---\n\nBody here.";

        var result = UpdateFileProcessor.ParseNewTicketFile(content);

        Assert.NotNull(result);
        Assert.Equal("My Quoted Title", result!.Value.Title);
    }

    [Fact]
    public void ParseNewTicketFile_SingleQuotedTitle_StripsQuotes()
    {
        var content = "---\ntitle: 'Single Quoted'\n---\n\nBody.";

        var result = UpdateFileProcessor.ParseNewTicketFile(content);

        Assert.NotNull(result);
        Assert.Equal("Single Quoted", result!.Value.Title);
    }

    [Fact]
    public void ParseNewTicketFile_NoFrontMatter_ReturnsNull()
    {
        var content = "Just plain content without front matter.";

        var result = UpdateFileProcessor.ParseNewTicketFile(content);

        Assert.Null(result);
    }

    [Fact]
    public void ParseNewTicketFile_NoTitle_ReturnsNull()
    {
        var content = "---\nauthor: someone\n---\n\nBody without a title.";

        var result = UpdateFileProcessor.ParseNewTicketFile(content);

        Assert.Null(result);
    }

    [Fact]
    public void ParseNewTicketFile_EmptyTitle_ReturnsNull()
    {
        var content = "---\ntitle:   \n---\n\nBody.";

        var result = UpdateFileProcessor.ParseNewTicketFile(content);

        Assert.Null(result);
    }

    [Fact]
    public void ParseNewTicketFile_UnclosedFrontMatter_ReturnsNull()
    {
        var content = "---\ntitle: No closing separator\n\nBody without closing dashes.";

        var result = UpdateFileProcessor.ParseNewTicketFile(content);

        Assert.Null(result);
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

    // ── Helpers ──────────────────────────────────────────────────────────────

    private string CreateUpdatesDir()
    {
        var dir = Path.Combine(_tempDir, ".aiboard", "updates");
        Directory.CreateDirectory(dir);
        return dir;
    }
}
