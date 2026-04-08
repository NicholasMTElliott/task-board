-- V12: Metrics support
-- Adds estimate column to agent_run, indexes for time-range queries,
-- and SQL views for operational metrics.

-- ── 1. Estimate column ───────────────────────────────────────────────────────
ALTER TABLE agent_run ADD COLUMN IF NOT EXISTS estimate NUMERIC NULL;

-- ── 2. Indexes for time-range queries (support --since filtering) ─────────────
CREATE INDEX IF NOT EXISTS idx_agent_run_started_at ON agent_run (started_at_utc);
CREATE INDEX IF NOT EXISTS idx_step_result_started_at ON step_result (started_at_utc);

-- ── 3. v_run_metrics: one row per completed agent run with derived columns ────
CREATE OR REPLACE VIEW v_run_metrics AS
SELECT
    run_id,
    card_id,
    state_name,
    agent_identity,
    git_branch,
    total_steps,
    completed_steps,
    outcome,
    error_detail,
    estimate,
    started_at_utc,
    completed_at_utc,
    EXTRACT(EPOCH FROM (completed_at_utc - started_at_utc))     AS duration_seconds,
    (outcome = 'COMPLETE')                                       AS is_complete,
    (outcome = 'ERROR')                                          AS is_error,
    (outcome = 'ERROR' AND (
        error_detail ILIKE '%rate limit%' OR
        error_detail ILIKE '%overloaded%'
    ))                                                           AS is_rate_limited
FROM agent_run
WHERE completed_at_utc IS NOT NULL;

-- ── 4. v_step_duration: one row per completed step with duration ──────────────
CREATE OR REPLACE VIEW v_step_duration AS
SELECT
    id,
    run_id,
    card_id,
    state_name,
    step_name,
    step_index,
    role,
    model,
    outcome,
    started_at_utc,
    completed_at_utc,
    EXTRACT(EPOCH FROM (completed_at_utc - started_at_utc)) AS duration_seconds
FROM step_result
WHERE completed_at_utc IS NOT NULL;

-- ── 5. v_card_metrics: one row per card with cycle/working/waiting time ───────
-- cycle_time  = last run end − first run start (wall-clock time including gates)
-- working_time = sum of individual run durations (actual agent execution time)
-- waiting_time = cycle_time − working_time (time waiting in review/approval)
CREATE OR REPLACE VIEW v_card_metrics AS
SELECT
    card_id,
    MIN(started_at_utc)                                          AS first_run_start,
    MAX(completed_at_utc)                                        AS last_run_end,
    EXTRACT(EPOCH FROM (MAX(completed_at_utc) - MIN(started_at_utc)))
                                                                 AS cycle_time_seconds,
    SUM(EXTRACT(EPOCH FROM (completed_at_utc - started_at_utc)))
                                                                 AS working_time_seconds,
    EXTRACT(EPOCH FROM (MAX(completed_at_utc) - MIN(started_at_utc))) -
        SUM(EXTRACT(EPOCH FROM (completed_at_utc - started_at_utc)))
                                                                 AS waiting_time_seconds,
    COUNT(*)                                                     AS total_runs,
    MAX(estimate)                                                AS estimate
FROM agent_run
WHERE completed_at_utc IS NOT NULL
GROUP BY card_id;

-- ── 6. v_card_rework: cards/states re-entered more than once ─────────────────
-- Only counts completed runs (outcome IS NOT NULL) to avoid false positives
-- from in-progress runs appearing alongside a completed run in the same state.
CREATE OR REPLACE VIEW v_card_rework AS
SELECT
    card_id,
    state_name,
    COUNT(*)         AS entry_count,
    COUNT(*) - 1     AS rework_count
FROM agent_run
WHERE outcome IS NOT NULL
GROUP BY card_id, state_name
HAVING COUNT(*) > 1;
