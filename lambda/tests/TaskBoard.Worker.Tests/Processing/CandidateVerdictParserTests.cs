using TaskBoard.Worker.Clients;
using TaskBoard.Worker.Models;
using TaskBoard.Worker.Processing;

namespace TaskBoard.Worker.Tests.Processing;

/// <summary>
/// Unit tests for <see cref="CandidateExecutor.ParseEvaluatorVerdict"/>. The
/// parser sees only the evaluator's <see cref="AgentResult"/>, so the inputs
/// here are realistic shapes the evaluator (a Claude/Codex/etc. agent) might
/// produce — fenced JSON, naked JSON at end of detail, prose with embedded
/// JSON, etc. Failures here cause silently wrong winner selection in prod, so
/// each scenario gets its own assertion.
/// </summary>
public class CandidateVerdictParserTests
{
    private static AgentResult ResultWithDetail(string detail, AgentOutcome outcome = AgentOutcome.COMPLETE)
        => new(outcome, detail);

    [Fact]
    public void Parse_FencedJsonWithWinnerAndScores_ExtractsAll()
    {
        var detail = """
            Candidate 1 had a cleaner diff with proper error handling.

            ```json
            {
              "outcome": "COMPLETE",
              "winner_index": 1,
              "scores": [
                { "index": 0, "score": 6.5, "reasoning": "Mostly works but skips the rate-limit case" },
                { "index": 1, "score": 8.5, "reasoning": "Cleaner, handles rate limits" }
              ]
            }
            ```
            """;

        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            ResultWithDetail(detail), candidateCount: 2, EvaluatorScoring.WinnerWithScores);

        Assert.Equal(1, verdict.WinnerIndex);
        Assert.Equal(2, verdict.Scores.Count);
        Assert.Equal(6.5m, verdict.Scores[0].Score);
        Assert.Equal(8.5m, verdict.Scores[1].Score);
        Assert.Contains("rate limit", verdict.Scores[1].Reasoning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_NakedJsonAtEnd_ExtractsAll()
    {
        var detail =
            "Candidate 0 wins on test coverage.\n\n" +
            "{\"outcome\":\"COMPLETE\",\"winner_index\":0,\"scores\":[{\"index\":0,\"score\":9}]}";

        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            ResultWithDetail(detail), candidateCount: 1, EvaluatorScoring.WinnerWithScores);

        Assert.Equal(0, verdict.WinnerIndex);
        Assert.Single(verdict.Scores);
        Assert.Equal(9m, verdict.Scores[0].Score);
    }

    [Fact]
    public void Parse_OutcomeNotComplete_ReturnsNoWinner()
    {
        // NEEDS_INFO / ERROR evaluators must never produce a winner — even if
        // they accidentally include a winner_index in their structured output.
        var detail = "{\"outcome\":\"NEEDS_INFO\",\"winner_index\":0}";

        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            new AgentResult(AgentOutcome.NEEDS_INFO, detail),
            candidateCount: 2,
            EvaluatorScoring.WinnerWithScores);

        Assert.Null(verdict.WinnerIndex);
        Assert.Empty(verdict.Scores);
    }

    [Fact]
    public void Parse_WinnerIndexOutOfRange_ReturnsNullWinner()
    {
        // Evaluator hallucinated a winner index past the candidate count. Don't
        // promote anyone — caller will treat as "no winner picked".
        var detail = "{\"outcome\":\"COMPLETE\",\"winner_index\":5}";

        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            ResultWithDetail(detail), candidateCount: 2, EvaluatorScoring.WinnerWithScores);

        Assert.Null(verdict.WinnerIndex);
    }

    [Fact]
    public void Parse_NegativeWinnerIndex_ReturnsNullWinner()
    {
        var detail = "{\"outcome\":\"COMPLETE\",\"winner_index\":-1}";

        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            ResultWithDetail(detail), candidateCount: 2, EvaluatorScoring.WinnerWithScores);

        Assert.Null(verdict.WinnerIndex);
    }

    [Fact]
    public void Parse_NoJsonInDetail_ReturnsNoWinner()
    {
        // Pure prose, no JSON at all. Caller falls back to "no winner promoted".
        var detail = "Candidate 1 is much better.";

        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            ResultWithDetail(detail), candidateCount: 2, EvaluatorScoring.WinnerWithScores);

        Assert.Null(verdict.WinnerIndex);
        Assert.Empty(verdict.Scores);
    }

    [Fact]
    public void Parse_NullDetail_ReturnsNoWinner()
    {
        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            new AgentResult(AgentOutcome.COMPLETE, Detail: null),
            candidateCount: 2,
            EvaluatorScoring.WinnerWithScores);

        Assert.Null(verdict.WinnerIndex);
        Assert.Empty(verdict.Scores);
    }

    [Fact]
    public void Parse_WinnerOnlyMode_DoesNotEmitScores()
    {
        // EvaluatorScoring.WinnerOnly: ignore any scores the evaluator emits.
        // Reduces noise in step_result rows when the operator only needs win/loss.
        var detail = """
            {"outcome":"COMPLETE","winner_index":0,"scores":[{"index":0,"score":7}]}
            """;

        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            ResultWithDetail(detail), candidateCount: 2, EvaluatorScoring.WinnerOnly);

        Assert.Equal(0, verdict.WinnerIndex);
        Assert.Empty(verdict.Scores);
    }

    [Fact]
    public void Parse_ScoreWithReasoningButNoNumber_StillRecorded()
    {
        // Evaluator forgot to include a numeric score for one candidate but
        // gave reasoning. Record the row anyway — the absence of score is data.
        var detail = """
            {"outcome":"COMPLETE","winner_index":1,"scores":[
              {"index":0,"reasoning":"weak design"},
              {"index":1,"score":9,"reasoning":"strong"}
            ]}
            """;

        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            ResultWithDetail(detail), candidateCount: 2, EvaluatorScoring.WinnerWithScores);

        Assert.Equal(1, verdict.WinnerIndex);
        Assert.Equal(2, verdict.Scores.Count);
        Assert.Null(verdict.Scores[0].Score);
        Assert.Equal("weak design", verdict.Scores[0].Reasoning);
    }

    [Fact]
    public void Parse_CompleteOutcomeButNoWinnerIndex_ReturnsNullWinner()
    {
        // Evaluator forgot or refused to pick a winner but still emitted
        // outcome=COMPLETE. Treat as "no winner promoted" — the canonical
        // worktree must NOT be reset to a candidate's branch in this case.
        var detail = "{\"outcome\":\"COMPLETE\",\"detail\":\"Both candidates are equivalent.\"}";

        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            ResultWithDetail(detail), candidateCount: 2, EvaluatorScoring.WinnerWithScores);

        Assert.Null(verdict.WinnerIndex);
    }

    [Fact]
    public void Parse_NegativeScore_DropsScoreKeepsReasoning()
    {
        // Evaluator emits a nonsense negative score. Don't persist garbage that
        // would skew avg_quality_score in v_provider_role_metrics. The reasoning
        // is still captured — its absence-of-score is itself a useful signal.
        var detail = """
            {"outcome":"COMPLETE","winner_index":0,"scores":[
              {"index":0,"score":-3,"reasoning":"weird candidate"}
            ]}
            """;

        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            ResultWithDetail(detail), candidateCount: 1, EvaluatorScoring.WinnerWithScores);

        Assert.Equal(0, verdict.WinnerIndex);
        Assert.Single(verdict.Scores);
        Assert.Null(verdict.Scores[0].Score);
        Assert.Equal("weird candidate", verdict.Scores[0].Reasoning);
    }

    [Fact]
    public void Parse_ScoreAbove10_DroppedAsOutOfRange()
    {
        // Same rationale as negative scores — evaluator broke the contract,
        // refuse to amplify the noise into the metrics views.
        var detail = """
            {"outcome":"COMPLETE","winner_index":0,"scores":[{"index":0,"score":42}]}
            """;

        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            ResultWithDetail(detail), candidateCount: 1, EvaluatorScoring.WinnerWithScores);

        Assert.Single(verdict.Scores);
        Assert.Null(verdict.Scores[0].Score);
    }

    [Fact]
    public void Parse_DuplicateIndex_KeepsLastOccurrence()
    {
        // Evaluator drafts then revises a score for the same candidate. Keep
        // the final answer ("last write wins") rather than persisting both —
        // each candidate row in step_result corresponds to one candidate, so
        // there's only one slot for a verdict per index anyway. The DB-level
        // UNIQUE INDEX (V19) would otherwise fail loudly on this shape.
        var detail = """
            {"outcome":"COMPLETE","winner_index":0,"scores":[
              {"index":0,"score":3,"reasoning":"first thought"},
              {"index":0,"score":8,"reasoning":"on reflection, much better"}
            ]}
            """;

        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            ResultWithDetail(detail), candidateCount: 1, EvaluatorScoring.WinnerWithScores);

        Assert.Single(verdict.Scores);
        Assert.Equal(8m, verdict.Scores[0].Score);
        Assert.Contains("on reflection", verdict.Scores[0].Reasoning);
    }

    [Fact]
    public void Parse_MultipleJsonObjects_TakesFirstWithWinnerIndex()
    {
        // Models sometimes draft a structure then revise it. The fenced block
        // (preferred) appears first in the parser's iteration order, so verdict
        // tracks that. If only naked JSON exists, the *last* balanced object
        // wins — matching OpenCodeOutputParser's "last wins" rule.
        var detail = """
            ```json
            {"outcome":"COMPLETE","winner_index":1,"scores":[{"index":1,"score":9}]}
            ```

            (Earlier draft, ignore: {"outcome":"NEEDS_INFO"})
            """;

        var verdict = CandidateExecutor.ParseEvaluatorVerdict(
            ResultWithDetail(detail), candidateCount: 2, EvaluatorScoring.WinnerWithScores);

        Assert.Equal(1, verdict.WinnerIndex);
    }
}
