-- V15: Multi-tenant partitioning
-- Adds a tenant_id column to all per-tenant tables so multiple projects
-- (different GitHub repos/projects, Trello boards, future providers) can
-- share one Postgres instance without colliding or blending data.
--
-- Tenant ID format: "{provider}:{provider-specific-identifier}", e.g.
--   github:owner/repo/4
--   trello:abc123def
--   stub:test
--
-- This migration drops and recreates the affected tables. Existing data is
-- not preserved (authorized; pre-multi-tenant deployments had no concept of
-- tenant). Dependent views (v_run_metrics, v_step_duration, v_card_metrics,
-- v_card_rework) are recreated by V16.

DROP VIEW IF EXISTS v_card_rework;
DROP VIEW IF EXISTS v_card_metrics;
DROP VIEW IF EXISTS v_step_duration;
DROP VIEW IF EXISTS v_run_metrics;

DROP TABLE IF EXISTS step_result;
DROP TABLE IF EXISTS agent_run;
DROP TABLE IF EXISTS card_state;
DROP TABLE IF EXISTS processed_events;

-- ── agent_run ───────────────────────────────────────────────────────────────
CREATE TABLE agent_run (
    tenant_id           TEXT NOT NULL,
    run_id              TEXT NOT NULL,
    card_id             TEXT NOT NULL,
    state_name          TEXT NOT NULL,
    agent_identity      TEXT NOT NULL,
    git_branch          TEXT NULL,
    total_steps         INT NOT NULL,
    completed_steps     INT NOT NULL DEFAULT 0,
    outcome             TEXT NULL,
    error_detail        TEXT NULL,
    failure_reason      TEXT NULL,
    estimate            NUMERIC NULL,
    session_startup_ms  INTEGER NULL,
    started_at_utc      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at_utc    TIMESTAMPTZ NULL,
    PRIMARY KEY (tenant_id, run_id),
    CONSTRAINT agent_run_outcome_check
        CHECK (outcome IS NULL OR outcome IN ('COMPLETE', 'NEEDS_INFO', 'ERROR')),
    CONSTRAINT agent_run_failure_reason_check
        CHECK (failure_reason IS NULL OR failure_reason IN ('RATE_LIMIT', 'AGENT_ERROR', 'INFRASTRUCTURE', 'TIMEOUT'))
);

CREATE INDEX idx_agent_run_tenant_card        ON agent_run (tenant_id, card_id);
CREATE INDEX idx_agent_run_tenant_card_state  ON agent_run (tenant_id, card_id, state_name);
CREATE INDEX idx_agent_run_tenant_started     ON agent_run (tenant_id, started_at_utc);

-- ── step_result ─────────────────────────────────────────────────────────────
CREATE TABLE step_result (
    tenant_id           TEXT NOT NULL,
    id                  UUID NOT NULL DEFAULT gen_random_uuid(),
    run_id              TEXT NOT NULL,
    card_id             TEXT NOT NULL,
    state_name          TEXT NOT NULL,
    step_name           TEXT NOT NULL,
    step_index          INT NOT NULL,
    role                TEXT NOT NULL,
    model               TEXT NOT NULL,
    outcome             TEXT NOT NULL,
    summary             TEXT NULL,
    detail              TEXT NULL,
    reference_content   TEXT NULL,
    conversation_log    TEXT NULL,
    questions           JSONB NULL,
    requested_steps     JSONB NULL,
    started_at_utc      TIMESTAMPTZ NOT NULL,
    completed_at_utc    TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    session_exec_ms     INTEGER NULL,
    PRIMARY KEY (id),
    CONSTRAINT uq_step_result_tenant_run_step UNIQUE (tenant_id, run_id, step_name),
    CONSTRAINT step_result_outcome_check
        CHECK (outcome IN ('COMPLETE', 'NEEDS_INFO', 'ERROR')),
    CONSTRAINT fk_step_result_agent_run
        FOREIGN KEY (tenant_id, run_id) REFERENCES agent_run(tenant_id, run_id)
);

CREATE INDEX idx_step_result_tenant_run        ON step_result (tenant_id, run_id);
CREATE INDEX idx_step_result_tenant_card       ON step_result (tenant_id, card_id);
CREATE INDEX idx_step_result_tenant_card_state ON step_result (tenant_id, card_id, state_name);
CREATE INDEX idx_step_result_tenant_started    ON step_result (tenant_id, started_at_utc);

-- ── card_state ──────────────────────────────────────────────────────────────
CREATE TABLE card_state (
    tenant_id            TEXT NOT NULL,
    card_id              TEXT NOT NULL,
    last_processed_event TEXT NULL,
    current_lock         TEXT NULL,
    last_known_list      TEXT NULL,
    origin_list_id       TEXT NULL,
    waiting_on_human     BOOLEAN NOT NULL DEFAULT FALSE,
    claimed_at           TIMESTAMPTZ NULL,
    updated_at_utc       TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, card_id)
);

-- ── processed_events ────────────────────────────────────────────────────────
CREATE TABLE processed_events (
    tenant_id        TEXT NOT NULL,
    action_id        TEXT NOT NULL,
    processed_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (tenant_id, action_id)
);
