-- V14: Add failure_reason enum column to agent_run
-- Adds a structured failure_reason column to replace ILIKE string matching
-- in v_run_metrics for categorising error types.

-- ── 1. Add failure_reason column ─────────────────────────────────────────────
ALTER TABLE agent_run ADD COLUMN failure_reason TEXT NULL;

ALTER TABLE agent_run ADD CONSTRAINT agent_run_failure_reason_check
    CHECK (failure_reason IS NULL OR failure_reason IN ('RATE_LIMIT', 'AGENT_ERROR', 'INFRASTRUCTURE', 'TIMEOUT'));

-- ── 2. Update v_run_metrics to use failure_reason instead of ILIKE ────────────
DROP VIEW IF EXISTS v_run_metrics;
CREATE VIEW v_run_metrics AS
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
