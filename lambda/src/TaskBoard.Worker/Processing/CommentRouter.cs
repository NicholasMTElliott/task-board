using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Single entry point for posting agent comments. Looks up the per-kind
/// retention policy (workflow.json &gt; <c>rerun.comments.retentionPolicy</c>,
/// falling back to a hardcoded default) and routes to the appropriate
/// <see cref="ITaskBoardClient"/> primitive.
///
/// <para>Retention defaults (overridable per-kind via workflow config):</para>
/// <list type="bullet">
///   <item>Append: run / step / candidate / evaluator / gate / optional / cache_hit</item>
///   <item>Delete-and-repost: dependency_blocked / completion_progress / rate_limit_notice / shutdown_notice / cross_card_notification</item>
///   <item>Upsert: created_ticket_dedupe (true dedupe key, not status)</item>
/// </list>
/// Unknown kinds default to <see cref="CommentRetention.Append"/> so future kinds
/// don't get silently dropped — operators can override via config when they
/// need a different policy.
/// </summary>
public interface ICommentRouter
{
    /// <summary>
    /// Posts an agent comment, choosing append / delete-and-repost / upsert
    /// based on the per-kind retention policy.
    /// </summary>
    /// <param name="cardId">Target card.</param>
    /// <param name="kind">One of the <c>AiboardLogMarker.Kind*</c> constants.</param>
    /// <param name="body">Body content WITHOUT the marker line; the router prepends the marker.</param>
    /// <param name="marker">
    /// The full marker line (e.g. <c>&lt;!-- aiboard-log kind:dependency_blocked card:42 --&gt;</c>).
    /// Used for dedupe matching (delete-and-repost / upsert) AND prepended to
    /// the body for append. Caller is responsible for ensuring the marker is
    /// stable across calls when retention requires identity matching.
    /// </param>
    Task PostAsync(
        string cardId,
        string kind,
        string body,
        string marker,
        CancellationToken cancellationToken);
}

public sealed class CommentRouter(
    ITaskBoardClient boardClient,
    ILogger<CommentRouter> logger) : ICommentRouter
{
    private WorkflowConfig? _workflowConfig;

    /// <summary>
    /// Sets the workflow config used for per-kind policy overrides.
    /// Called from <c>Program.cs</c> after the config loads. The router can
    /// be used without a workflow config — every kind falls through to its
    /// hardcoded default. (Construction order in DI surfaces this gap;
    /// late-binding via this setter avoids a circular registration.)
    /// </summary>
    public void Bind(WorkflowConfig workflowConfig)
    {
        _workflowConfig = workflowConfig;
    }

    public async Task PostAsync(
        string cardId, string kind, string body, string marker,
        CancellationToken cancellationToken)
    {
        var retention = ResolveRetention(kind);

        switch (retention)
        {
            case CommentRetention.Append:
                {
                    var fullBody = $"{marker}\n{body}";
                    await boardClient.AppendAgentCommentAsync(cardId, fullBody, cancellationToken);
                    break;
                }

            case CommentRetention.DeleteAndRepost:
                {
                    // Delete prior matching comments first so the repost lands at
                    // the bottom of the chronological timeline. Best-effort —
                    // a delete failure doesn't block the new post.
                    try
                    {
                        await boardClient.DeleteAgentCommentsByMarkerAsync(cardId, marker, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex,
                            "Delete leg of delete-and-repost failed for kind={Kind} on card {CardId}; falling back to append-only — operator may see a stale duplicate at the top of the timeline",
                            kind, cardId);
                    }

                    var fullBody = $"{marker}\n{body}";
                    await boardClient.AppendAgentCommentAsync(cardId, fullBody, cancellationToken);
                    break;
                }

            case CommentRetention.Upsert:
                // Existing legacy contract: caller passes body sans marker; impl
                // prepends marker when creating, edits in place when found.
                await boardClient.UpsertAgentCommentAsync(cardId, body, marker, cancellationToken);
                break;

            default:
                logger.LogWarning(
                    "Unknown CommentRetention {Retention} for kind={Kind} on card {CardId}; defaulting to append",
                    retention, kind, cardId);
                {
                    var fullBody = $"{marker}\n{body}";
                    await boardClient.AppendAgentCommentAsync(cardId, fullBody, cancellationToken);
                }
                break;
        }
    }

    /// <summary>
    /// Resolves the retention policy for a comment kind. Workflow-config
    /// override wins; otherwise falls back to the hardcoded default.
    /// </summary>
    internal CommentRetention ResolveRetention(string kind)
    {
        var policy = _workflowConfig?.Rerun?.Comments?.RetentionPolicy;
        if (policy is not null && policy.TryGetValue(kind, out var configured))
        {
            var parsed = ParsePolicy(configured);
            if (parsed.HasValue)
                return parsed.Value;
            logger.LogWarning(
                "Unrecognised retention policy '{Configured}' for kind={Kind} in workflow config; using default",
                configured, kind);
        }

        return DefaultFor(kind);
    }

    /// <summary>
    /// Hardcoded defaults — the rerun redesign's "shipped sensible policy" so
    /// operators can leave the config block out entirely and still get the
    /// right behaviour. Overrideable per-kind in workflow.json.
    /// </summary>
    internal static CommentRetention DefaultFor(string kind) => kind switch
    {
        AiboardLogMarker.KindRun => CommentRetention.Append,
        AiboardLogMarker.KindStep => CommentRetention.Append,
        AiboardLogMarker.KindCandidate => CommentRetention.Append,
        AiboardLogMarker.KindEvaluator => CommentRetention.Append,
        AiboardLogMarker.KindGate => CommentRetention.Append,
        AiboardLogMarker.KindOptional => CommentRetention.Append,
        AiboardLogMarker.KindCacheHit => CommentRetention.Append,

        AiboardLogMarker.KindDependencyBlocked => CommentRetention.DeleteAndRepost,
        AiboardLogMarker.KindCompletionProgress => CommentRetention.DeleteAndRepost,
        AiboardLogMarker.KindRateLimitNotice => CommentRetention.DeleteAndRepost,
        AiboardLogMarker.KindShutdownNotice => CommentRetention.DeleteAndRepost,
        AiboardLogMarker.KindCrossCardNotification => CommentRetention.DeleteAndRepost,

        AiboardLogMarker.KindCreatedTicketDedupe => CommentRetention.Upsert,

        _ => CommentRetention.Append,
    };

    private static CommentRetention? ParsePolicy(string raw) => raw?.Trim().ToLowerInvariant() switch
    {
        "append" => CommentRetention.Append,
        "delete_and_repost" => CommentRetention.DeleteAndRepost,
        "upsert" => CommentRetention.Upsert,
        _ => null,
    };
}
