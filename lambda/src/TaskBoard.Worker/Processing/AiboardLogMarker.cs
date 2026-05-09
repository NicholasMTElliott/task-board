using System.Text;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Builder + classifier for the rerun-redesign comment marker shape.
///
/// Every agent-generated comment carries an HTML marker of the form
/// <c>&lt;!-- aiboard-log kind:KIND key:value [key:value...] --&gt;</c>.
/// The marker is the single source of truth for two questions:
/// (a) is this comment agent-generated or operator-authored?, used by the
/// hash builder to classify operator comments as input;
/// (b) which retention policy applies?, looked up by <c>kind</c> in
/// <c>workflow.json &gt; rerun.comments.retentionPolicy</c>.
///
/// Replaces the legacy per-shape markers (<c>agent-step:</c>, <c>agent-run:</c>,
/// <c>gate-check:</c>, <c>agent-rate-limit:</c>, <c>completion-check:</c>,
/// <c>agent-created-ticket:</c>, <c>agent-cross-comment:</c>,
/// <c>agent-dependency-blocked:</c>, <c>agent-shutdown:</c>) wholesale.
/// No legacy migration: existing development cards are recreated.
/// </summary>
internal static class AiboardLogMarker
{
    /// <summary>The literal prefix every aiboard-log marker starts with.</summary>
    internal const string MarkerPrefix = "<!-- aiboard-log ";

    // ── Comment kinds ────────────────────────────────────────────────

    // Append (chronological log entries): run / step / candidate / evaluator / gate /
    // optional / cache_hit. The retention policy table in workflow.json routes
    // these through ITaskBoardClient.AppendAgentCommentAsync by default.
    internal const string KindRun = "run";
    internal const string KindStep = "step";
    internal const string KindCandidate = "candidate";
    internal const string KindEvaluator = "evaluator";
    internal const string KindGate = "gate";
    internal const string KindOptional = "optional";
    internal const string KindCacheHit = "cache_hit";

    // Delete-and-repost (status notices that must stay current chronologically).
    // When the provider lacks a comment-delete API, the router falls back to
    // upsert-with-timestamp-prefix in the body so operators still see the
    // freshness of the latest update.
    internal const string KindDependencyBlocked = "dependency_blocked";
    internal const string KindCompletionProgress = "completion_progress";
    internal const string KindRateLimitNotice = "rate_limit_notice";
    internal const string KindShutdownNotice = "shutdown_notice";
    internal const string KindCrossCardNotification = "cross_card_notification";

    // True dedupe key (NOT a status). Stays upsert because a "ticket already
    // created" key being reposted N times would do the same thing N times.
    internal const string KindCreatedTicketDedupe = "created_ticket_dedupe";

    // ── Classifier ───────────────────────────────────────────────────

    /// <summary>
    /// Returns true if the given comment body contains any aiboard-log marker.
    /// Used by the hash builder to classify a comment as agent-generated
    /// (excluded from operator-comment input hash) vs operator-authored
    /// (included). Ordinal substring match on the marker prefix is the only
    /// classification rule — no per-kind allowlist, so future kinds are
    /// classified correctly without code changes.
    /// </summary>
    internal static bool IsAgentGenerated(string body)
        => body.Contains(MarkerPrefix, StringComparison.Ordinal);

    // ── Builder ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds an aiboard-log marker line. Field values containing spaces, double
    /// quotes, or backslashes are double-quoted with backslash escapes;
    /// everything else is emitted as <c>key:value</c> bare. Keys must not
    /// contain spaces or colons (caller's responsibility).
    /// </summary>
    /// <param name="kind">One of the <c>Kind*</c> constants on this class.</param>
    /// <param name="fields">Key/value pairs in the order they should appear after <c>kind:</c>.</param>
    internal static string Build(string kind, IEnumerable<KeyValuePair<string, string>>? fields = null)
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Kind is required", nameof(kind));

        var sb = new StringBuilder();
        sb.Append(MarkerPrefix).Append("kind:").Append(kind);

        if (fields is not null)
        {
            foreach (var kv in fields)
            {
                sb.Append(' ').Append(kv.Key).Append(':');
                AppendValue(sb, kv.Value);
            }
        }

        sb.Append(" -->");
        return sb.ToString();
    }

    private static void AppendValue(StringBuilder sb, string value)
    {
        // Empty-string values are emitted as quoted empty pair so re-parsers
        // can distinguish "field present, empty" from "field absent."
        if (value.Length == 0)
        {
            sb.Append("\"\"");
            return;
        }

        bool needsQuote = false;
        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c) || c == '"' || c == '\\' || c == '<' || c == '>')
            {
                needsQuote = true;
                break;
            }
        }

        if (!needsQuote)
        {
            sb.Append(value);
            return;
        }

        sb.Append('"');
        foreach (var c in value)
        {
            if (c == '\\' || c == '"') sb.Append('\\');
            sb.Append(c);
        }
        sb.Append('"');
    }
}

/// <summary>
/// How a comment of a given <see cref="AiboardLogMarker"/> kind should be
/// posted. Configured via <c>workflow.json &gt; rerun.comments.retentionPolicy</c>;
/// the orchestrator consults this enum (not a string) at call sites.
/// </summary>
public enum CommentRetention
{
    /// <summary>Always create a new comment. Chronological log shape.</summary>
    Append,

    /// <summary>Delete prior matching comment then append a new one. Status notices that should stay current.</summary>
    DeleteAndRepost,

    /// <summary>Edit the prior comment in place (creating it on first post). True dedupe keys.</summary>
    Upsert,
}
