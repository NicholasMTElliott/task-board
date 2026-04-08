ALTER TABLE agent_run ADD COLUMN failure_reason TEXT NULL;

ALTER TABLE agent_run ADD CONSTRAINT agent_run_failure_reason_check
    CHECK (failure_reason IS NULL OR failure_reason IN ('RATE_LIMIT', 'AGENT_ERROR', 'INFRASTRUCTURE', 'TIMEOUT'));
