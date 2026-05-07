using System.Text.RegularExpressions;
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
    ILogger<RerunPreambleBuilder> logger,
    ITaskBoardClient? boardClient = null)
{
    /// <summary>
    /// Matches `Created #NN` (with various capitalisations) inside the prior
    /// comment body. The orchestrator emits `- Created #{id} — {title}` from
    /// <see cref="AgentRunner.FormatUpdateSummary"/>; agents commonly echo the
    /// same shape ("Created #43") in their verdict. Anything that looks like a
    /// claim of a ticket creation gets verified against the board before we
    /// trust the preamble.
    /// </summary>
    private static readonly Regex CreatedTicketClaimPattern =
        new(@"\bcreated\s*#(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        // Verify any "Created #N" claims in the prior body actually exist on
        // the board. The fast-path is only safe when the prior output's
        // load-bearing facts are still true. The most common fictional claim
        // is "Created #43, #44" emitted by an agent whose UpdateFileProcessor
        // silently rejected its files (missing `new-` prefix). When the
        // claimed tickets don't exist, suppress the preamble and let the step
        // run fresh — which is the safe outcome.
        if (boardClient is not null)
        {
            var verification = await VerifyCreatedTicketClaimsAsync(
                priorBody, cardId, stepName, cancellationToken);
            if (verification.Suppress)
            {
                return null;
            }
        }

        return Format(variant, priorBody);
    }

    /// <summary>
    /// Result of scanning the prior comment body for "Created #N" claims and
    /// resolving each against the board. <see cref="Suppress"/> is true when
    /// any cited ticket does not exist on the board — meaning the prior
    /// output's "I created these" claim is fictional and we should not let
    /// the next agent confirm it sight-unseen.
    /// </summary>
    private readonly record struct ClaimVerificationResult(bool Suppress, int TotalCited, int Missing);

    private async Task<ClaimVerificationResult> VerifyCreatedTicketClaimsAsync(
        string priorBody,
        string cardId,
        string stepName,
        CancellationToken cancellationToken)
    {
        if (boardClient is null)
            return new ClaimVerificationResult(false, 0, 0);

        var matches = CreatedTicketClaimPattern.Matches(priorBody);
        if (matches.Count == 0)
            return new ClaimVerificationResult(false, 0, 0);

        // Dedupe — same #N often shows up in multiple comment sections.
        var citedIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in matches)
        {
            if (m.Groups.Count > 1) citedIds.Add(m.Groups[1].Value);
        }

        var missing = new List<string>();
        foreach (var id in citedIds)
        {
            try
            {
                _ = await boardClient.GetCardAsync(id, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                // Treat any lookup failure (404, transient API error, parse
                // error) as "missing". Conservative: if we can't confirm the
                // ticket exists, don't trust the prior claim. A flaky API
                // makes the preamble suppress; that's a safe default — the
                // step just runs fresh, paying tokens but not making a wrong
                // decision.
                missing.Add(id);
            }
        }

        if (missing.Count > 0)
        {
            logger.LogWarning(
                "Re-run preamble suppressed for step '{StepName}' on card {CardId}: prior comment cited " +
                "{TotalCount} ticket(s) but {MissingCount} could not be resolved on the board ({Missing}). " +
                "The prior 'Created #N' claim is fictional or stale — running step fresh instead of letting " +
                "the agent confirm fabricated work.",
                stepName, cardId, citedIds.Count, missing.Count, string.Join(", ", missing.Select(id => "#" + id)));
            return new ClaimVerificationResult(true, citedIds.Count, missing.Count);
        }

        logger.LogDebug(
            "Re-run preamble verification for step '{StepName}' on card {CardId}: all {Count} cited ticket(s) exist on the board",
            stepName, cardId, citedIds.Count);
        return new ClaimVerificationResult(false, citedIds.Count, 0);
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
