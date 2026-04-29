using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;

namespace TaskBoard.Worker.Processing;

/// <summary>
/// Selects the wording variant for the re-run preamble. The detection
/// criteria are identical for both; only the instructive text differs.
/// </summary>
public enum PreambleVariant
{
    /// <summary>Preamble for the agent that produced the prior output (single-agent
    /// step or one of N parallel candidates). Tells the agent: confirm prior or
    /// produce updates.</summary>
    TaskPrompt,

    /// <summary>Preamble for the evaluator that picks among candidates. Tells the
    /// evaluator: if all candidates confirmed prior, pick any with winner_index 0;
    /// otherwise evaluate normally.</summary>
    Evaluator,
}

/// <summary>
/// Builds a "this is a re-run; bail with COMPLETE if nothing relevant changed"
/// preamble for agent task prompts on a re-run of a previously-completed step.
/// </summary>
/// <remarks>
/// <para>
/// Two signals must both be present for the preamble to be injected:
/// </para>
/// <list type="number">
///   <item>A comment with the step's marker is currently on the card. The
///   operator's force-rerun lever is to delete that comment, which suppresses
///   the preamble and causes the step to run fresh.</item>
///   <item>The most recent <c>step_result</c> row for
///   <c>(cardId, stateName, stepName)</c> from a run other than the current
///   one has <c>outcome = COMPLETE</c>. Prior NEEDS_INFO or ERROR outcomes do
///   not qualify — those need to re-run normally.</item>
/// </list>
/// <para>
/// Returning <c>null</c> means "no fast-path; run as normal". Callers should
/// prepend the returned string to their task prompt only when non-null.
/// </para>
/// </remarks>
public sealed class RerunPreambleBuilder(
    IRunStore runStore,
    ILogger<RerunPreambleBuilder> logger)
{
    /// <summary>
    /// Looks for a re-run signal and returns a formatted preamble if one applies.
    /// </summary>
    /// <param name="cardId">Card under execution.</param>
    /// <param name="stateName">Current workflow state.</param>
    /// <param name="stepName">Canonical step name (e.g. <c>"create_design"</c>,
    /// <c>"gate_check"</c>, <c>"optional:security_review"</c>). Multi-slot
    /// candidate rows with <c>:slot-N:cand-N:provider</c> suffixes and the
    /// evaluator row with <c>:evaluator</c> suffix are matched automatically.</param>
    /// <param name="markerName">Marker fragment to look for in comment bodies
    /// (e.g. <c>"agent-step:create_design"</c>, <c>"gate-check:Ready for Design"</c>,
    /// <c>"agent-step:optional:security_review"</c>). The full marker on the card
    /// is <c>&lt;!-- {markerName} --&gt;</c>.</param>
    /// <param name="currentRunId">Run id of the in-flight run. Excluded from the
    /// prior-result lookup so we don't accidentally treat the current run's own
    /// rows as "prior".</param>
    /// <param name="existingComments">Comments fetched from the card (typically
    /// already in scope for the caller).</param>
    /// <param name="variant">Instructive-text shape: candidate task vs evaluator.</param>
    public async Task<string?> TryBuildPreambleAsync(
        string cardId,
        string stateName,
        string stepName,
        string markerName,
        string currentRunId,
        IReadOnlyList<CardComment> existingComments,
        PreambleVariant variant,
        CancellationToken cancellationToken)
    {
        var fullMarker = $"<!-- {markerName} -->";
        var priorComment = FindLatestCommentWithMarker(existingComments, fullMarker);
        if (priorComment is null)
        {
            // Either first run, or operator deleted the comment to force a fresh run.
            return null;
        }

        var priorBody = StripMarker(priorComment.Body, fullMarker);
        if (string.IsNullOrWhiteSpace(priorBody))
        {
            logger.LogDebug(
                "Re-run preamble suppressed for step '{StepName}' on card {CardId}: prior comment body empty after marker strip",
                stepName, cardId);
            return null;
        }

        IReadOnlyList<StepResultRecord> records;
        try
        {
            records = await runStore.GetStepResultsForCardAsync(cardId, stateName, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Re-run preamble suppressed for step '{StepName}' on card {CardId}: step_result lookup failed",
                stepName, cardId);
            return null;
        }

        var priorRecord = FindMostRecentMatchingRecord(records, stepName, currentRunId);
        if (priorRecord is null)
        {
            logger.LogWarning(
                "Re-run marker '{Marker}' present on card {CardId} but no matching step_result row " +
                "from a prior run was found. Treating as fresh run.",
                fullMarker, cardId);
            return null;
        }

        if (priorRecord.Outcome != AgentOutcome.COMPLETE)
        {
            // NEEDS_INFO or ERROR — step needs to re-run normally.
            return null;
        }

        return Format(variant, priorBody);
    }

    private static CardComment? FindLatestCommentWithMarker(
        IReadOnlyList<CardComment> comments, string fullMarker)
    {
        // Defensive: if the upsert path ever produced duplicates, take the most recent.
        CardComment? latest = null;
        foreach (var c in comments)
        {
            if (c.Body is null) continue;
            if (!c.Body.Contains(fullMarker, StringComparison.Ordinal)) continue;
            if (latest is null || c.CreatedAt > latest.CreatedAt) latest = c;
        }
        return latest;
    }

    private static string StripMarker(string body, string fullMarker)
    {
        // Remove the first occurrence of the marker (and the newline that follows
        // it, if any) so the prior content reads cleanly when embedded in the preamble.
        var idx = body.IndexOf(fullMarker, StringComparison.Ordinal);
        if (idx < 0) return body.Trim();

        var before = body[..idx];
        var afterStart = idx + fullMarker.Length;
        var after = afterStart < body.Length ? body[afterStart..] : string.Empty;
        if (after.StartsWith('\r')) after = after[1..];
        if (after.StartsWith('\n')) after = after[1..];

        var stripped = (before + after).Trim();
        return stripped;
    }

    /// <summary>
    /// Finds the most recent <c>step_result</c> row whose name matches the
    /// canonical step name, excluding any from <paramref name="currentRunId"/>.
    /// Matches handle three shapes:
    /// <list type="bullet">
    ///   <item>Exact: <c>step.Name</c> (single-agent step, gate check, optional step).</item>
    ///   <item>Single-slot evaluator: <c>step.Name:evaluator</c>.</item>
    ///   <item>Multi-slot evaluator: <c>step.Name:slot-N:evaluator</c>.</item>
    /// </list>
    /// Records arrive ordered by <c>completed_at_utc DESC</c> from
    /// <see cref="IRunStore.GetStepResultsForCardAsync"/>, so the first match is
    /// the most recent.
    /// </summary>
    private static StepResultRecord? FindMostRecentMatchingRecord(
        IReadOnlyList<StepResultRecord> records, string stepName, string currentRunId)
    {
        foreach (var record in records)
        {
            if (record.RunId == currentRunId) continue;
            if (Matches(record.StepName, stepName))
                return record;
        }
        return null;
    }

    private static bool Matches(string recordStepName, string canonicalStepName)
    {
        if (recordStepName == canonicalStepName) return true;
        if (recordStepName == $"{canonicalStepName}:evaluator") return true;
        if (recordStepName.StartsWith($"{canonicalStepName}:slot-", StringComparison.Ordinal)
            && recordStepName.EndsWith(":evaluator", StringComparison.Ordinal))
            return true;
        return false;
    }

    private static string Format(PreambleVariant variant, string priorBody)
    {
        var instructions = variant switch
        {
            PreambleVariant.Evaluator =>
                "The prior winning output for this step is below. Each candidate has been told this is a re-run; " +
                "some may have confirmed the prior output (with `detail: \"Confirmed prior output remains accurate.\"` " +
                "and no changes), others may have proposed updates in response to new context.\n\n" +
                "If all candidates confirmed the prior output, pick any one as the winner (`winner_index: 0` is fine) " +
                "and propagate `outcome: COMPLETE` with " +
                "`detail: \"All candidates confirmed prior output remains accurate.\"`\n\n" +
                "Otherwise, evaluate normally — comparing the candidates' refreshed outputs against the prior output " +
                "and against each other.",
            _ /* TaskPrompt */ =>
                "Your prior output for this step is below. Since then, this card may have received new comments or " +
                "answered questions. Read the latest conversation history and consider whether anything material has changed.\n\n" +
                "If your prior output remains accurate and complete given the latest context, respond with " +
                "`outcome: COMPLETE` and `detail: \"Confirmed prior output remains accurate.\"`. " +
                "Do not redo any analysis or restate the prior output — just confirm.\n\n" +
                "If the latest context materially changes your conclusions (e.g. references a new ticket, " +
                "contradicts a prior assumption, requires an updated design), produce a refreshed output as you would on a first pass.",
        };

        return
            "## RE-RUN OF PREVIOUSLY COMPLETED STEP\n\n" +
            instructions + "\n\n" +
            "---\n" +
            "PRIOR OUTPUT:\n\n" +
            priorBody + "\n" +
            "---";
    }
}
