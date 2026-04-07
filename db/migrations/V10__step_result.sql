-- One row per agent step execution within a run.
-- Covers mandatory steps, gate checks (step_name = 'gate_check'),
-- and optional steps (step_name = 'optional:{name}').
--
-- Column semantics:
--   summary          = AgentResult.Detail (agent's outcome summary text)
--   detail           = full task file body after this step executed
--   reference_content = content from {cardId}-reference.md update files (agent-directed reference material)
--   conversation_log = full agent conversation (omitted from board when DB is available)
--   questions        = JSONB array of {question, recommendations} objects
--   requested_steps  = JSONB array of step name strings (gate check output)
--
-- card_id and state_name are denormalized from agent_run for query efficiency
-- (avoids join in GetStepResultsForCardAsync).
CREATE TABLE step_result (
    id                  UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    run_id              TEXT NOT NULL REFERENCES agent_run(run_id),
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
    CONSTRAINT step_result_outcome_check
        CHECK (outcome IN ('COMPLETE', 'NEEDS_INFO', 'ERROR'))
);

-- Uniqueness on (run_id, step_name) ensures idempotent inserts via ON CONFLICT DO NOTHING.
-- Step names are guaranteed unique within a run (gate_check, optional:{name}, etc.).
ALTER TABLE step_result
    ADD CONSTRAINT uq_step_result_run_step UNIQUE (run_id, step_name);

CREATE INDEX idx_step_result_run_id ON step_result (run_id);
CREATE INDEX idx_step_result_card_id ON step_result (card_id);
CREATE INDEX idx_step_result_card_state ON step_result (card_id, state_name);
