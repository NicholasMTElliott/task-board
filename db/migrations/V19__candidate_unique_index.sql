-- V19: Tighten candidate-group integrity.
--
-- V17 added candidate_group_id + candidate_index but left the "exactly one row
-- per (group, index)" rule to application code. That makes v_candidate_outcomes
-- and v_provider_role_metrics double-count if the runtime ever inserts a
-- duplicate (e.g. retry-on-error path inadvertently re-saves a candidate row).
-- A unique index makes the duplicate fail loudly at the DB instead of producing
-- silently-wrong metrics.
--
-- Partial index — only enforced for candidate rows. Traditional single-agent
-- step rows have candidate_group_id NULL and are not constrained by this rule.

CREATE UNIQUE INDEX IF NOT EXISTS uq_step_result_candidate_slot
    ON step_result (tenant_id, candidate_group_id, candidate_index)
    WHERE candidate_group_id IS NOT NULL;
