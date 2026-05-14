using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Unit tests for the three-layer verdict extraction in
/// <see cref="CandidateExecutor.ParseEvaluatorVerdict"/>:
/// (1) structured fields on <see cref="AgentResult"/>,
/// (2) detail-embedded JSON,
/// (3) prose patterns in detail markdown.
///
/// <para>
/// The whole reason this exists: a downstream project card #3 v0.0.16/17 evaluator runs
/// produced clear verdicts in markdown ("Candidate 1 wins", scoreboard
/// with "**Winner.**") but skipped the structured winner_index field,
/// and the orchestrator routed to ERROR because it only knew how to
/// re-extract from detail JSON. The prose fallback recovers those.
/// </para>
/// </summary>
public class CandidateExecutorVerdictParserTests
{
    // ── Layer 1: structured fields ───────────────────────────────────────────

    [Fact]
    public void StructuredWinnerIndex_TakesPrecedence_OverDetailJson()
    {
        // Conflict scenario: structured field says 0, embedded JSON says 1.
        // Structured field is the schema-validated source — it wins.
        var result = new AgentResult(
            Outcome: AgentOutcome.COMPLETE,
            Detail: """And in JSON: ```json {"winner_index": 1} ```""",
            WinnerIndex: 0);

        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            result, candidateCount: 2, EvaluatorScoring.WinnerOnly);

        Assert.Equal(0, verdict.WinnerIndex);
    }

    [Fact]
    public void StructuredScores_TakesPrecedence_OverDetailJson()
    {
        var result = new AgentResult(
            Outcome: AgentOutcome.COMPLETE,
            Detail: """```json {"scores":[{"index":0,"score":3},{"index":1,"score":3}]} ```""",
            WinnerIndex: 1,
            Scores: new[]
            {
                new EvaluatorScore(0, 7m, "from structured"),
                new EvaluatorScore(1, 9m, "winner from structured"),
            });

        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            result, candidateCount: 2, EvaluatorScoring.WinnerWithScores);

        Assert.Equal(2, verdict.Scores.Count);
        Assert.Equal(7m, verdict.Scores[0].Score);
        Assert.Equal("from structured", verdict.Scores[0].Reasoning);
        Assert.Equal(9m, verdict.Scores[1].Score);
    }

    // ── Layer 2: detail-embedded JSON ────────────────────────────────────────

    [Fact]
    public void DetailEmbeddedJson_UsedWhenStructuredWinnerIndexAbsent()
    {
        var result = new AgentResult(
            Outcome: AgentOutcome.COMPLETE,
            Detail: """
                Verdict in JSON below.
                ```json
                {"winner_index": 1, "scores": [{"index":0,"score":4},{"index":1,"score":8}]}
                ```
                """);

        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            result, candidateCount: 2, EvaluatorScoring.WinnerWithScores);

        Assert.Equal(1, verdict.WinnerIndex);
        Assert.Equal(2, verdict.Scores.Count);
    }

    // ── Layer 3: prose patterns ──────────────────────────────────────────────

    [Theory]
    [InlineData("**Verdict: Candidate 1 wins**\nReasoning text here.", 1)]
    [InlineData("Candidate 0 wins because of better tests.", 0)]
    [InlineData("Winner: Candidate 2\n\nReasoning...", 2)]
    [InlineData("After review, Candidate 3 is the winner.", 3)]
    [InlineData("Candidate 1 wins. Candidate 2 wins (no, that was a typo).", 1)] // first match
    public void ProseExtractor_RecoversWinnerFromCommonPhrasings(string prose, int expectedWinner)
    {
        var winner = CandidateExecutor.TryExtractWinnerFromProse(prose, candidateCount: 4);
        Assert.Equal(expectedWinner, winner);
    }

    [Fact]
    public void ProseExtractor_RecoversWinnerFromScoreboardWinnerMarker()
    {
        // The exact shape a downstream project card #3's v0.0.17 evaluator produced.
        var detail =
            "## Verdict\n\n" +
            "Candidate 1 had the better implementation map.\n\n" +
            "| # | Provider | Score | Notes |\n" +
            "|---|----------|-------|-------|\n" +
            "| 0 | claude-opus-4-6 | 6 | Good but generic |\n" +
            "| 1 | claude-sonnet-4-6 | **7** | **Winner.** Best implementation map |\n" +
            "| 2 | claude-haiku-4-5 | 4 | Too brief |\n";

        var winner = CandidateExecutor.TryExtractWinnerFromProse(detail, candidateCount: 3);
        Assert.Equal(1, winner);
    }

    [Fact]
    public void ProseExtractor_ScoreboardWithoutWinnerMarker_ReturnsNull()
    {
        // Bare scoreboard table without a row marked Winner is ambiguous.
        // Return null rather than picking the highest score (which would be a
        // judgment call this fallback isn't equipped to make).
        var detail =
            "| # | Score |\n" +
            "|---|-------|\n" +
            "| 0 | 6 |\n" +
            "| 1 | 7 |\n";

        var winner = CandidateExecutor.TryExtractWinnerFromProse(detail, candidateCount: 2);
        Assert.Null(winner);
    }

    [Fact]
    public void ProseExtractor_NoWinnerNamingPhrasing_ReturnsNull()
    {
        var detail =
            "Both candidates produced acceptable work.\n" +
            "I cannot pick decisively without more context.\n";

        var winner = CandidateExecutor.TryExtractWinnerFromProse(detail, candidateCount: 2);
        Assert.Null(winner);
    }

    [Fact]
    public void ProseExtractor_OutOfRangeIndex_ReturnsNull()
    {
        // Bounds-check: prose says "Candidate 5 wins" but only 2 candidates ran.
        // Treat as malformed rather than picking the highest valid index.
        var detail = "Candidate 5 wins.";
        var winner = CandidateExecutor.TryExtractWinnerFromProse(detail, candidateCount: 2);
        Assert.Null(winner);
    }

    [Fact]
    public void ProseExtractor_NullOrEmpty_ReturnsNull()
    {
        Assert.Null(CandidateExecutor.TryExtractWinnerFromProse(null, candidateCount: 2));
        Assert.Null(CandidateExecutor.TryExtractWinnerFromProse("", candidateCount: 2));
        Assert.Null(CandidateExecutor.TryExtractWinnerFromProse("   ", candidateCount: 2));
    }

    // ── Integration through ParseEvaluatorVerdict ────────────────────────────

    [Fact]
    public void ParseEvaluatorVerdict_StructuredAbsent_DetailJsonAbsent_ProseRecoversWinner()
    {
        // The full failure shape: model returned COMPLETE but no structured
        // winner_index, no JSON in detail, only prose with a clear verdict.
        // Layer 3 (prose) is what saves the run from routing to ERROR.
        var result = new AgentResult(
            Outcome: AgentOutcome.COMPLETE,
            Detail:
                "## Verdict: Candidate 1 wins\n\n" +
                "Candidate 1 had the strongest analysis.");

        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            result, candidateCount: 3, EvaluatorScoring.WinnerOnly);

        Assert.Equal(1, verdict.WinnerIndex);
    }

    [Fact]
    public void ParseEvaluatorVerdict_NonCompleteOutcome_ReturnsNullWinnerRegardlessOfProse()
    {
        // Even if the prose says "Candidate 1 wins", a NEEDS_INFO/ERROR outcome
        // means the evaluator didn't commit to a verdict — don't synthesise one
        // from prose. Mirrors the existing short-circuit in the verdict parser.
        var result = new AgentResult(
            Outcome: AgentOutcome.NEEDS_INFO,
            Detail: "Candidate 1 wins, but I need more info.");

        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            result, candidateCount: 2, EvaluatorScoring.WinnerOnly);

        Assert.Null(verdict.WinnerIndex);
    }
}
