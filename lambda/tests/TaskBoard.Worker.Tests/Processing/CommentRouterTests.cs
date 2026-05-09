using Microsoft.Extensions.Logging.Abstractions;
using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Pin tests for <see cref="CommentRouter"/> — the rerun-redesign service that
/// routes agent comments through Append / DeleteAndRepost / Upsert based on
/// the per-kind retention policy. Each test asserts both the operations
/// invoked AND their order (delete-and-repost MUST delete before append, or
/// the new comment can land above the stale one if the deletion races).
/// </summary>
public class CommentRouterTests
{
    private static readonly NullLogger<CommentRouter> _log = NullLogger<CommentRouter>.Instance;

    [Fact]
    public async Task DefaultPolicies_StepKind_RoutesToAppend()
    {
        // Step comments are chronological log entries — append-only. The
        // retention default is Append; verify the router picks it up without
        // any workflow config override.
        var board = new RecordingBoardClient();
        var router = new CommentRouter(board, _log);

        await router.PostAsync("42", AiboardLogMarker.KindStep, "step body",
            "<!-- aiboard-log kind:step run:abc -->", CancellationToken.None);

        Assert.Equal(1, board.AppendCount);
        Assert.Equal(0, board.UpsertCount);
        Assert.Equal(0, board.DeleteCount);
        // Body must include the marker line at the top.
        Assert.Contains("<!-- aiboard-log kind:step run:abc -->", board.LastAppendedBody);
        Assert.Contains("step body", board.LastAppendedBody);
    }

    [Fact]
    public async Task DefaultPolicies_DependencyBlocked_DeletesPriorThenAppends()
    {
        // Status notices that should stay current chronologically use
        // delete-and-repost so the most recent state is at the bottom of the
        // timeline. Order matters: delete must come BEFORE append so the new
        // comment lands at the bottom.
        var board = new RecordingBoardClient();
        var router = new CommentRouter(board, _log);

        await router.PostAsync("42", AiboardLogMarker.KindDependencyBlocked,
            "blocked by #5", "<!-- aiboard-log kind:dependency_blocked card:42 -->",
            CancellationToken.None);

        Assert.Equal(1, board.DeleteCount);
        Assert.Equal(1, board.AppendCount);
        Assert.Equal(0, board.UpsertCount);
        Assert.Equal("Delete,Append", string.Join(',', board.OperationOrder));
    }

    [Fact]
    public async Task DefaultPolicies_CreatedTicketDedupe_UsesUpsert()
    {
        // True dedupe key (created_ticket_dedupe) — replacing every time
        // would do the same thing every time, so retain Upsert semantics.
        var board = new RecordingBoardClient();
        var router = new CommentRouter(board, _log);

        await router.PostAsync("42", AiboardLogMarker.KindCreatedTicketDedupe,
            "created #99", "<!-- aiboard-log kind:created_ticket_dedupe slug:fix-foo -->",
            CancellationToken.None);

        Assert.Equal(1, board.UpsertCount);
        Assert.Equal(0, board.AppendCount);
        Assert.Equal(0, board.DeleteCount);
    }

    [Fact]
    public async Task UnknownKind_FallsBackToAppend()
    {
        // Future kinds we haven't enumerated should default to append (visible)
        // rather than be silently dropped or upsert-stomped.
        var board = new RecordingBoardClient();
        var router = new CommentRouter(board, _log);

        await router.PostAsync("42", "future_kind_we_havent_thought_of",
            "body", "<!-- aiboard-log kind:future_kind_we_havent_thought_of x:y -->",
            CancellationToken.None);

        Assert.Equal(1, board.AppendCount);
        Assert.Equal(0, board.UpsertCount);
        Assert.Equal(0, board.DeleteCount);
    }

    [Fact]
    public async Task WorkflowConfigOverride_FlipsKindFromAppendToUpsert()
    {
        // Operator can override per-kind via workflow.json. Test: force
        // kind:step → upsert (silly but exercises the override path).
        var board = new RecordingBoardClient();
        var router = new CommentRouter(board, _log);
        var config = new WorkflowConfig(
            States: new(),
            Roles: new(),
            Rerun: new RerunConfig(Comments: new RerunCommentsConfig(
                RetentionPolicy: new()
                {
                    [AiboardLogMarker.KindStep] = "upsert",
                })));
        router.Bind(config);

        await router.PostAsync("42", AiboardLogMarker.KindStep, "body",
            "<!-- aiboard-log kind:step run:abc -->", CancellationToken.None);

        Assert.Equal(0, board.AppendCount);
        Assert.Equal(1, board.UpsertCount);
    }

    [Fact]
    public async Task DeleteFailure_DoesNotBlockAppend()
    {
        // The delete-leg of delete-and-repost is best-effort. A board API
        // failure on delete must not prevent the new comment from landing —
        // operators would rather see a duplicate than a missing status.
        var board = new RecordingBoardClient { ThrowOnDelete = true };
        var router = new CommentRouter(board, _log);

        await router.PostAsync("42", AiboardLogMarker.KindDependencyBlocked,
            "blocked", "<!-- aiboard-log kind:dependency_blocked card:42 -->",
            CancellationToken.None);

        Assert.Equal(1, board.AppendCount); // Append still ran
    }

    [Fact]
    public async Task UnrecognisedRetentionPolicyValue_FallsBackToDefault()
    {
        // Operator typo in workflow.json → log a warning and use the default
        // for that kind. Doesn't crash the run.
        var board = new RecordingBoardClient();
        var router = new CommentRouter(board, _log);
        var config = new WorkflowConfig(
            States: new(),
            Roles: new(),
            Rerun: new RerunConfig(Comments: new RerunCommentsConfig(
                RetentionPolicy: new()
                {
                    [AiboardLogMarker.KindStep] = "delete_with_extreme_prejudice",
                })));
        router.Bind(config);

        await router.PostAsync("42", AiboardLogMarker.KindStep, "body",
            "<!-- aiboard-log kind:step run:abc -->", CancellationToken.None);

        Assert.Equal(1, board.AppendCount); // Default for step is Append
    }

    [Fact]
    public void DefaultFor_AllAppendKinds_AreAppend()
    {
        Assert.Equal(CommentRetention.Append, CommentRouter.DefaultFor(AiboardLogMarker.KindStep));
        Assert.Equal(CommentRetention.Append, CommentRouter.DefaultFor(AiboardLogMarker.KindRun));
        Assert.Equal(CommentRetention.Append, CommentRouter.DefaultFor(AiboardLogMarker.KindCandidate));
        Assert.Equal(CommentRetention.Append, CommentRouter.DefaultFor(AiboardLogMarker.KindEvaluator));
        Assert.Equal(CommentRetention.Append, CommentRouter.DefaultFor(AiboardLogMarker.KindGate));
        Assert.Equal(CommentRetention.Append, CommentRouter.DefaultFor(AiboardLogMarker.KindOptional));
        Assert.Equal(CommentRetention.Append, CommentRouter.DefaultFor(AiboardLogMarker.KindCacheHit));
    }

    [Fact]
    public void DefaultFor_AllStatusNoticeKinds_AreDeleteAndRepost()
    {
        Assert.Equal(CommentRetention.DeleteAndRepost, CommentRouter.DefaultFor(AiboardLogMarker.KindDependencyBlocked));
        Assert.Equal(CommentRetention.DeleteAndRepost, CommentRouter.DefaultFor(AiboardLogMarker.KindCompletionProgress));
        Assert.Equal(CommentRetention.DeleteAndRepost, CommentRouter.DefaultFor(AiboardLogMarker.KindRateLimitNotice));
        Assert.Equal(CommentRetention.DeleteAndRepost, CommentRouter.DefaultFor(AiboardLogMarker.KindShutdownNotice));
        Assert.Equal(CommentRetention.DeleteAndRepost, CommentRouter.DefaultFor(AiboardLogMarker.KindCrossCardNotification));
    }

    [Fact]
    public void DefaultFor_DedupeKind_IsUpsert()
    {
        Assert.Equal(CommentRetention.Upsert, CommentRouter.DefaultFor(AiboardLogMarker.KindCreatedTicketDedupe));
    }

    private sealed class RecordingBoardClient : ITaskBoardClient
    {
        public int AppendCount { get; private set; }
        public int UpsertCount { get; private set; }
        public int DeleteCount { get; private set; }
        public string? LastAppendedBody { get; private set; }
        public List<string> OperationOrder { get; } = new();
        public bool ThrowOnDelete { get; set; }

        public Task AppendAgentCommentAsync(string cardId, string commentBody, CancellationToken ct)
        {
            AppendCount++;
            LastAppendedBody = commentBody;
            OperationOrder.Add("Append");
            return Task.CompletedTask;
        }

        public Task DeleteAgentCommentsByMarkerAsync(string cardId, string markerSubstring, CancellationToken ct)
        {
            DeleteCount++;
            OperationOrder.Add("Delete");
            if (ThrowOnDelete)
                throw new InvalidOperationException("simulated board API failure");
            return Task.CompletedTask;
        }

        public Task UpsertAgentCommentAsync(string cardId, string body, string marker, CancellationToken ct)
        {
            UpsertCount++;
            OperationOrder.Add("Upsert");
            return Task.CompletedTask;
        }

        // Unused by the router; minimal stubs.
        public Task<BoardCard> GetCardAsync(string cardId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<BoardCard>> GetBoardCardsAsync(string boardId, CancellationToken ct, IReadOnlyList<string>? excludeStatuses = null) => throw new NotImplementedException();
        public Task UpdateCardBodyAsync(string cardId, string body, CancellationToken ct) => throw new NotImplementedException();
        public Task MoveCardToColumnAsync(string cardId, string columnId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<CardComment>> GetCardCommentsAsync(string cardId, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> CreateCardAsync(CreateCardRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task AddLabelAsync(string cardId, string labelName, CancellationToken ct) => throw new NotImplementedException();
        public Task RemoveLabelAsync(string cardId, string labelName, CancellationToken ct) => throw new NotImplementedException();
        public Task AssignAsync(string cardId, string username, CancellationToken ct) => throw new NotImplementedException();
        public Task UnassignAsync(string cardId, string? username, CancellationToken ct) => throw new NotImplementedException();
        public Task SetFieldAsync(string cardId, string fieldName, string value, CancellationToken ct) => throw new NotImplementedException();
        public Task ClearFieldAsync(string cardId, string fieldName, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> GetCurrentUserAsync(CancellationToken ct) => throw new NotImplementedException();
    }
}
