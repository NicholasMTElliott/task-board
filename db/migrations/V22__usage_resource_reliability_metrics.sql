-- V22: Comprehensive metrics expansion. One migration covers:
--
--   step_result columns:
--     - cost_usd                  (Claude only; null for Codex / local LLMs)
--     - input_tokens / output_tokens
--     - cache_read_tokens / cache_creation_tokens (Claude only)
--     - fast_path_hit             (re-run preamble was injected AND outcome was COMPLETE)
--     - structurer_fallback_used  (DockerOpenCode no-think structurer recovered the result)
--     - evaluator_prompt_chars    (set on evaluator step rows; chars not tokens to avoid tokenizer dep)
--     - winner_regressed          (set true on selected=true rows when same-run gate check fails)
--
--   agent_run columns:
--     - rate_limit_events         (counter; incremented when AgentRunner catches RateLimitException)
--
--   View updates:
--     - v_step_duration / v_candidate_outcomes recreated to project the new columns
--     - v_provider_role_metrics gains cost / token / structurer aggregates
--     - new v_evaluator_reliability for evaluator regression-rate by (role, provider)
--     - new v_fast_path_hit_rate for re-run efficiency by (state, step)
--
-- All new step_result columns are nullable so backfilling existing data isn't needed.
-- rate_limit_events defaults to 0 so existing rows get a sensible value.

ALTER TABLE step_result ADD COLUMN cost_usd NUMERIC(10, 6);
ALTER TABLE step_result ADD COLUMN input_tokens BIGINT;
ALTER TABLE step_result ADD COLUMN output_tokens BIGINT;
ALTER TABLE step_result ADD COLUMN cache_read_tokens BIGINT;
ALTER TABLE step_result ADD COLUMN cache_creation_tokens BIGINT;
ALTER TABLE step_result ADD COLUMN fast_path_hit BOOLEAN;
ALTER TABLE step_result ADD COLUMN structurer_fallback_used BOOLEAN;
ALTER TABLE step_result ADD COLUMN evaluator_prompt_chars INT;
ALTER TABLE step_result ADD COLUMN winner_regressed BOOLEAN;

ALTER TABLE agent_run ADD COLUMN rate_limit_events INT NOT NULL DEFAULT 0;

-- Recreate v_step_duration with the new columns.
-- Same drop-then-create reason as V18 / V21: CREATE OR REPLACE VIEW won't reorder
-- columns mid-list and the existing column order would force the new fields to the
-- end, which is fine functionally but confusing to read.
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
    cost_usd,
    input_tokens,
    output_tokens,
    cache_read_tokens,
    cache_creation_tokens,
    fast_path_hit,
    structurer_fallback_used,
    evaluator_prompt_chars,
    winner_regressed,
    started_at_utc,
    completed_at_utc,
    EXTRACT(EPOCH FROM (completed_at_utc - started_at_utc)) AS duration_seconds
FROM step_result
WHERE completed_at_utc IS NOT NULL;

-- v_candidate_outcomes recreated for the same reason.
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
    cost_usd,
    input_tokens,
    output_tokens,
    cache_read_tokens,
    cache_creation_tokens,
    structurer_fallback_used,
    winner_regressed,
    started_at_utc,
    completed_at_utc,
    EXTRACT(EPOCH FROM (completed_at_utc - started_at_utc)) AS duration_seconds
FROM step_result
WHERE candidate_group_id IS NOT NULL
  AND completed_at_utc IS NOT NULL;

-- v_provider_role_metrics: extend with cost / token / structurer aggregates.
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
                                                                AS avg_duration_seconds,
    SUM(cost_usd)                                               AS total_cost_usd,
    AVG(cost_usd) FILTER (WHERE cost_usd IS NOT NULL)           AS avg_cost_usd,
    SUM(input_tokens)                                           AS total_input_tokens,
    SUM(output_tokens)                                          AS total_output_tokens,
    AVG(input_tokens) FILTER (WHERE input_tokens IS NOT NULL)   AS avg_input_tokens,
    AVG(output_tokens) FILTER (WHERE output_tokens IS NOT NULL) AS avg_output_tokens,
    SUM(cache_read_tokens)                                      AS total_cache_read_tokens,
    SUM(cache_creation_tokens)                                  AS total_cache_creation_tokens,
    COUNT(*) FILTER (WHERE structurer_fallback_used = true)     AS structurer_fallback_count,
    CASE
        WHEN COUNT(*) = 0 THEN NULL
        ELSE 100.0 *
            COUNT(*) FILTER (WHERE structurer_fallback_used = true) /
            COUNT(*)
    END                                                         AS structurer_fallback_rate_percent
FROM step_result
WHERE candidate_group_id IS NOT NULL
  AND completed_at_utc IS NOT NULL
GROUP BY tenant_id, role, provider;

-- v_evaluator_reliability: per-(evaluator-role, evaluator-provider) regression rate.
-- An evaluator step row has step_name ending in ':evaluator' and candidate_group_id IS NULL.
-- Its picked winner shows up as a sibling step_result with selected=true, same run_id, same
-- state_name, same step_index, AND same slot_index (the multi-slot fallback infix —
-- IS NOT DISTINCT FROM handles the NULL=NULL case for single-slot setups). Without
-- step_index + slot_index in the join, a multi-slot run where slot 0's evaluator
-- failed (no winner) and slot 1's succeeded would cross-attribute slot 1's winner
-- to slot 0's evaluator, inflating the verdict count and corrupting regression rate.
-- We also filter to evaluators whose own outcome was COMPLETE — only verdict-bearing
-- evaluator runs count as "verdicts." Evaluators that returned ERROR (couldn't pick
-- a winner) aren't valid attribution targets.
CREATE OR REPLACE VIEW v_evaluator_reliability AS
WITH evaluator_steps AS (
    SELECT
        tenant_id,
        run_id,
        state_name,
        step_index,
        slot_index,
        role         AS evaluator_role,
        provider     AS evaluator_provider,
        model        AS evaluator_model
    FROM step_result
    WHERE step_name LIKE '%:evaluator'
      AND candidate_group_id IS NULL
      AND outcome = 'COMPLETE'
), winners AS (
    SELECT
        sr.tenant_id,
        sr.run_id,
        sr.state_name,
        sr.step_index,
        sr.slot_index,
        sr.winner_regressed
    FROM step_result sr
    WHERE sr.selected = true
      AND sr.candidate_group_id IS NOT NULL
)
SELECT
    e.tenant_id,
    e.evaluator_role,
    e.evaluator_provider,
    COUNT(*)                                                          AS total_verdicts,
    COUNT(*) FILTER (WHERE w.winner_regressed = true)                 AS regressed_count,
    CASE
        WHEN COUNT(*) FILTER (WHERE w.winner_regressed IS NOT NULL) = 0 THEN NULL
        ELSE 100.0 *
            COUNT(*) FILTER (WHERE w.winner_regressed = true) /
            COUNT(*) FILTER (WHERE w.winner_regressed IS NOT NULL)
    END                                                                AS regression_rate_percent
FROM evaluator_steps e
LEFT JOIN winners w
       ON w.tenant_id  = e.tenant_id
      AND w.run_id     = e.run_id
      AND w.state_name = e.state_name
      AND w.step_index = e.step_index
      AND w.slot_index IS NOT DISTINCT FROM e.slot_index
GROUP BY e.tenant_id, e.evaluator_role, e.evaluator_provider;

-- v_fast_path_hit_rate: how often does the re-run preamble actually short-circuit a step?
-- High hit rate = the feature is paying off. Zero hit rate on a step that re-runs often means
-- something is preventing the fast path (operator deletes comments? markers diverging?).
CREATE OR REPLACE VIEW v_fast_path_hit_rate AS
SELECT
    tenant_id,
    state_name,
    step_name,
    role,
    provider,
    COUNT(*)                                              AS total_invocations,
    COUNT(*) FILTER (WHERE fast_path_hit = true)          AS fast_path_hits,
    CASE
        WHEN COUNT(*) = 0 THEN NULL
        ELSE 100.0 *
            COUNT(*) FILTER (WHERE fast_path_hit = true) /
            COUNT(*)
    END                                                    AS hit_rate_percent
FROM step_result
WHERE completed_at_utc IS NOT NULL
GROUP BY tenant_id, state_name, step_name, role, provider;
