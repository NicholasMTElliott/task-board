-- Execution envelope for a single agent run on a card state.
-- One row per AgentRunner.ExecuteAsync() invocation.
-- outcome is NULL while in progress; set to COMPLETE/NEEDS_INFO/ERROR on completion.
CREATE TABLE agent_run (
    run_id              TEXT PRIMARY KEY,
    card_id             TEXT NOT NULL,
    state_name          TEXT NOT NULL,
    agent_identity      TEXT NOT NULL,
    git_branch          TEXT NULL,
    total_steps         INT NOT NULL,
    completed_steps     INT NOT NULL DEFAULT 0,
    outcome             TEXT NULL,
    error_detail        TEXT NULL,
    started_at_utc      TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    completed_at_utc    TIMESTAMPTZ NULL,
    CONSTRAINT agent_run_outcome_check
        CHECK (outcome IS NULL OR outcome IN ('COMPLETE', 'NEEDS_INFO', 'ERROR'))
);

CREATE INDEX idx_agent_run_card_id ON agent_run (card_id);
CREATE INDEX idx_agent_run_card_state ON agent_run (card_id, state_name);
