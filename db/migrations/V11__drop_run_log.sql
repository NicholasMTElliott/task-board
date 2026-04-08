-- V11: Drop unused run_log table.
-- Replaced by agent_run (V9) and step_result (V10) which were introduced in #38.
-- The run_log table was never written to by any application code.
DROP TABLE IF EXISTS run_log;
