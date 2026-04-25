-- V18: Recreate v_step_duration with the V17 candidate columns and add
--      two new views for per-(role, provider) and head-to-head metrics.
--
-- v_step_duration gets candidate_group_id / candidate_index / provider /
-- selected / quality_score so PgMetricsStore can answer "duration by provider"
-- and Grafana can render per-provider panels without a separate join.
--
-- v_provider_role_metrics aggregates win rate, quality score, and runtime by
-- (role, provider). Excludes the evaluator's own step_result rows
-- (candidate_group_id IS NULL on the evaluator) so wins / total only counts
-- candidate runs.
--
-- v_candidate_outcomes is the per-row companion: one row per candidate
-- execution, joining the candidate's step_result with the evaluator's
-- decision. Useful for ad-hoc queries and head-to-head reports.

CREATE OR REPLACE VIEW v_step_duration AS
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
    selected,
    quality_score,
    started_at_utc,
    completed_at_utc,
    EXTRACT(EPOCH FROM (completed_at_utc - started_at_utc)) AS duration_seconds
FROM step_result
WHERE completed_at_utc IS NOT NULL;

-- One row per candidate execution.
-- Surfaces the evaluator's verdict alongside the candidate's own outcome so
-- callers don't have to re-derive "did this candidate win" logic.
CREATE OR REPLACE VIEW v_candidate_outcomes AS
SELECT
    tenant_id,
    candidate_group_id,
    run_id,
    card_id,
    state_name,
    step_name,
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

-- Aggregated per (role, provider): the headline metrics for "is provider X
-- worth keeping for role Y?" decisions.
--   total_runs        — number of times this (role, provider) participated as a candidate
--   wins              — selected = true count (NULL when evaluator hasn't run yet is excluded)
--   win_rate_percent  — wins / runs_with_decision; NULL when no decisions yet
--   avg_quality_score — mean evaluator score (NULL when no scores recorded)
--   avg_duration_secs — mean wall time per candidate run
CREATE OR REPLACE VIEW v_provider_role_metrics AS
SELECT
    tenant_id,
    role,
    provider,
    COUNT(*)                                                   AS total_runs,
    COUNT(*) FILTER (WHERE selected = true)                    AS wins,
    COUNT(*) FILTER (WHERE selected IS NOT NULL)               AS runs_with_decision,
    CASE
        WHEN COUNT(*) FILTER (WHERE selected IS NOT NULL) = 0 THEN NULL
        ELSE 100.0 *
            COUNT(*) FILTER (WHERE selected = true) /
            COUNT(*) FILTER (WHERE selected IS NOT NULL)
    END                                                         AS win_rate_percent,
    AVG(quality_score) FILTER (WHERE quality_score IS NOT NULL) AS avg_quality_score,
    AVG(EXTRACT(EPOCH FROM (completed_at_utc - started_at_utc)))
                                                                AS avg_duration_seconds
FROM step_result
WHERE candidate_group_id IS NOT NULL
  AND completed_at_utc IS NOT NULL
GROUP BY tenant_id, role, provider;
