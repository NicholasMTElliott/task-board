-- V16: Recreate metrics views with tenant_id
-- All views now project tenant_id so callers can scope queries to the
-- running process's tenant (PgMetricsStore appends WHERE tenant_id = $X).

CREATE OR REPLACE VIEW v_run_metrics AS
SELECT
    tenant_id,
    run_id,
    card_id,
    state_name,
    agent_identity,
    git_branch,
    total_steps,
    completed_steps,
    outcome,
    error_detail,
    failure_reason,
    estimate,
    started_at_utc,
    completed_at_utc,
    EXTRACT(EPOCH FROM (completed_at_utc - started_at_utc))     AS duration_seconds,
    (outcome = 'COMPLETE')                                       AS is_complete,
    (outcome = 'ERROR')                                          AS is_error,
    (failure_reason = 'RATE_LIMIT')                              AS is_rate_limited
FROM agent_run
WHERE completed_at_utc IS NOT NULL;

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
    outcome,
    started_at_utc,
    completed_at_utc,
    EXTRACT(EPOCH FROM (completed_at_utc - started_at_utc)) AS duration_seconds
FROM step_result
WHERE completed_at_utc IS NOT NULL;

CREATE OR REPLACE VIEW v_card_metrics AS
SELECT
    tenant_id,
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
GROUP BY tenant_id, card_id;

CREATE OR REPLACE VIEW v_card_rework AS
SELECT
    tenant_id,
    card_id,
    state_name,
    COUNT(*)         AS entry_count,
    COUNT(*) - 1     AS rework_count
FROM agent_run
WHERE outcome IS NOT NULL
GROUP BY tenant_id, card_id, state_name
HAVING COUNT(*) > 1;
