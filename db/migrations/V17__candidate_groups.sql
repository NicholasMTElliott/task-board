-- V17: Candidate-group metadata on step_result for multi-agent evaluation.
--
-- Adds the columns needed to record N candidate executions of the same step
-- (different providers/models, different worktrees) plus the evaluator's
-- decision (winner + scores). The plain-step case (single executor, no
-- evaluator) is unchanged: candidate_group_id is NULL and the rest of the
-- new columns stay NULL.
--
-- Interpretation:
--   candidate_group_id NULL  → traditional single-agent step (unchanged behaviour)
--   candidate_group_id SET   → one of N competing candidates; candidate_index 0..N-1
--   provider                 → executor that ran this row (always populated for new rows;
--                              backfill uses 'claude-cli' for historical rows so existing
--                              metrics queries don't see NULL after the migration)
--   selected = true          → won the evaluator
--   selected = false         → ran but lost
--   selected NULL            → no evaluator ran yet, or this is not a candidate row
--   quality_score            → evaluator-assigned 0.00–10.00 (NULL when no evaluator ran)
--   evaluator_reasoning      → optional free-text from the evaluator about this candidate
--
-- The "exactly one selected = true per (tenant_id, candidate_group_id)" rule
-- is enforced in application code (AgentRunner) rather than at the DB layer
-- because rows are inserted before the evaluator runs.

ALTER TABLE step_result
    ADD COLUMN candidate_group_id   UUID            NULL,
    ADD COLUMN candidate_index      INT             NULL,
    ADD COLUMN provider             TEXT            NULL,
    ADD COLUMN selected             BOOLEAN         NULL,
    ADD COLUMN quality_score        NUMERIC(4,2)    NULL,
    ADD COLUMN evaluator_reasoning  TEXT            NULL;

-- Backfill historical rows so provider-aware metrics queries don't see NULL.
-- Pre-V17 the only routed providers were claude-cli (default), docker-claude-cli,
-- and codex. We can't tell which was used after the fact, so we use the wire-on
-- default 'claude-cli' as a placeholder. Operators who care about historical
-- attribution should write their own backfill before relying on the metrics.
UPDATE step_result
SET provider = 'claude-cli'
WHERE provider IS NULL;

ALTER TABLE step_result
    ALTER COLUMN provider SET NOT NULL;

-- Partial index: lookups for "all candidates in group X" / "winner of group X"
-- only matter when candidate_group_id is set. Filtering keeps the index small
-- so the no-candidates path (the common case) doesn't pay for it.
CREATE INDEX IF NOT EXISTS idx_step_result_candidate_group
    ON step_result (tenant_id, candidate_group_id)
    WHERE candidate_group_id IS NOT NULL;

-- Sanity constraint: candidate_index without a group, or vice versa, is a bug.
ALTER TABLE step_result
    ADD CONSTRAINT step_result_candidate_pair_chk
        CHECK (
            (candidate_group_id IS NULL AND candidate_index IS NULL)
         OR (candidate_group_id IS NOT NULL AND candidate_index IS NOT NULL)
        );
