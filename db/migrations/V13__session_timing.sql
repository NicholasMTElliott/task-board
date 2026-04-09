-- V13: Add session timing columns for container reuse metrics
-- session_startup_ms: time (ms) to create and start the container for a run (one-time per run)
-- session_exec_ms: time (ms) for each step execution via docker exec (per step)
-- Both are nullable; NULL means the step/run did not use a session (per-step docker run).

ALTER TABLE agent_run
    ADD COLUMN session_startup_ms integer;

ALTER TABLE step_result
    ADD COLUMN session_exec_ms integer;
