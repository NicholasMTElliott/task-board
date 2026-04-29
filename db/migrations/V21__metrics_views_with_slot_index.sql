-- V21: Recreate v_step_duration and v_candidate_outcomes to project slot_index,
--      and add v_slot_outcomes for "how often does slot 0 carry the day vs
--      needing fallback?" metrics.
--
-- v_provider_role_metrics is intentionally NOT changed — its aggregation by
-- (role, provider) is independent of which slot a candidate ran in.
--
-- v_step_duration must be dropped before recreate: Postgres CREATE OR REPLACE
-- VIEW refuses to insert a new column mid-list, only to append at the end.
-- The existing v_step_duration column order doesn't have slot_index, so a
-- straight replace would error with "cannot change name of view column ...".
-- Same logic as V18 used.
DROP VIEW IF EXISTS v_step_duration;

CREATE VIEW v_step_duration AS
SELECT
    tenant_id,
    id,
    run_id,
    card_id,
    state_name,
    step_name,
    step_index,
    role,
    model,
    provider,
    outcome,
    candidate_group_id,
    candidate_index,
    slot_index,
    selected,
    quality_score,
    started_at_utc,
    completed_at_utc,
    EXTRACT(EPOCH FROM (completed_at_utc - started_at_utc)) AS duration_seconds
FROM step_result
WHERE completed_at_utc IS NOT NULL;

-- v_candidate_outcomes: same recreation reason — mid-list column.
DROP VIEW IF EXISTS v_candidate_outcomes;

CREATE VIEW v_candidate_outcomes AS
SELECT
    tenant_id,
    candidate_group_id,
    run_id,
    card_id,
    state_name,
    step_name,
    slot_index,
    candidate_index,
    role,
    provider,
    model,
    outcome,
    selected,
    quality_score,
    evaluator_reasoning,
    started_at_utc,
    completed_at_utc,
    EXTRACT(EPOCH FROM (completed_at_utc - started_at_utc)) AS duration_seconds
FROM step_result
WHERE candidate_group_id IS NOT NULL
  AND completed_at_utc IS NOT NULL;

-- v_slot_outcomes: per (state, step, slot) aggregate of how often each slot
-- "won" the step (had its winner promoted), needed fallback, or failed.
--
-- A slot "won" when at least one of its candidates has selected = true.
-- A slot "failed" when every candidate row in that group has selected = false
-- or NULL (no winner picked / evaluator error / promotion failure). A slot
-- with at least one selected = false candidate AND no selected = true
-- candidate is treated as failed.
--
-- This view is the answer to "is slot 0 actually carrying the day, or is the
-- fallback chain doing all the work?" — useful for tuning slot ordering and
-- confirming a primary provider is reliable enough to keep at the front.
CREATE OR REPLACE VIEW v_slot_outcomes AS
WITH per_group AS (
    SELECT
        tenant_id,
        candidate_group_id,
        state_name,
        step_index,
        slot_index,
        BOOL_OR(selected = true) AS slot_won
    FROM step_result
    WHERE candidate_group_id IS NOT NULL
      AND slot_index IS NOT NULL
    GROUP BY tenant_id, candidate_group_id, state_name, step_index, slot_index
)
SELECT
    tenant_id,
    state_name,
    step_index,
    slot_index,
    COUNT(*)                                              AS total_invocations,
    COUNT(*) FILTER (WHERE slot_won = true)               AS wins,
    COUNT(*) FILTER (WHERE slot_won IS NULL OR slot_won = false) AS failures,
    CASE
        WHEN COUNT(*) = 0 THEN NULL
        ELSE 100.0 * COUNT(*) FILTER (WHERE slot_won = true) / COUNT(*)
    END                                                   AS win_rate_percent
FROM per_group
GROUP BY tenant_id, state_name, step_index, slot_index;
