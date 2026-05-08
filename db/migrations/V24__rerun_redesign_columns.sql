-- V24: Rerun-redesign schema columns (Problem 2 substrate + Problem 1 plumbing).
--
-- Problem 2 — description-as-state / comments-as-log:
--
--   step_result columns:
--     - section_output_hash    (TEXT; SHA-256 of the step's normalized managed section,
--                               cached for the cache-decision path in Problem 1)
--     - section_update_json    (JSONB; raw agent-supplied section_update directive,
--                               kept for replay/debugging)
--
-- Problem 1 — deterministic skip detection (forward-compatible columns; not yet
-- written to in this commit, but the schema lands here so Problem 1 can be a
-- pure code change):
--
--   step_result columns:
--     - execution_kind         (TEXT; "full_run" | "cache_hit"; default 'full_run')
--     - source_run_id          (TEXT; for cache_hit rows, the prior run's run_id)
--     - source_step_result_id  (UUID; for cache_hit rows, the prior step_result.id)
--     - input_hash             (TEXT; SHA-256 of the step's input bundle)
--     - output_summary         (TEXT; agent's brief summary; complements the existing
--                               `summary` column which is sometimes the full detail)
--
-- Problem 3 — gate-check awareness (forward-compatible column; not yet written
-- to in this commit):
--
--   agent_run columns:
--     - state_entry_canonical_sha  (TEXT; the canonical worktree's HEAD SHA at the
--                                  IN_PROGRESS column transition; copied forward
--                                  to subsequent runs in the same state)
--
-- All new step_result columns are nullable so backfilling existing data is unnecessary.
-- execution_kind has a default so existing rows are classified as full_run retroactively.

ALTER TABLE step_result ADD COLUMN section_output_hash TEXT;
ALTER TABLE step_result ADD COLUMN section_update_json JSONB;
ALTER TABLE step_result ADD COLUMN execution_kind TEXT NOT NULL DEFAULT 'full_run';
ALTER TABLE step_result ADD COLUMN source_run_id TEXT;
ALTER TABLE step_result ADD COLUMN source_step_result_id UUID;
ALTER TABLE step_result ADD COLUMN input_hash TEXT;
ALTER TABLE step_result ADD COLUMN output_summary TEXT;

ALTER TABLE agent_run ADD COLUMN state_entry_canonical_sha TEXT;

-- Indexes: the cache-decision path (Problem 1) queries
-- step_result by (tenant_id, card_id, state_name, step_name) to find the most
-- recent COMPLETE for the cache key. Existing index `idx_step_result_card_state`
-- already covers (tenant_id, card_id, state_name); a tighter step-aware index
-- speeds the lookup further. input_hash + section_output_hash get covering
-- columns so the cache decision can hit a single index page.
CREATE INDEX idx_step_result_cache_lookup
    ON step_result (tenant_id, card_id, state_name, step_name, completed_at_utc DESC)
    INCLUDE (input_hash, section_output_hash, outcome, run_id);
