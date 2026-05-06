CREATE TABLE IF NOT EXISTS card_dependency_wait (
    tenant_id TEXT NOT NULL,
    card_id TEXT NOT NULL,
    blocker_card_id TEXT NOT NULL,
    source TEXT NOT NULL,
    first_seen_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    last_seen_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    resolved_at_utc TIMESTAMPTZ NULL,
    last_blocker_title TEXT NULL,
    last_blocker_column TEXT NULL,
    PRIMARY KEY (tenant_id, card_id, blocker_card_id, source)
);

CREATE INDEX IF NOT EXISTS idx_card_dependency_wait_open_card
    ON card_dependency_wait (tenant_id, card_id)
    WHERE resolved_at_utc IS NULL;

CREATE INDEX IF NOT EXISTS idx_card_dependency_wait_open_blocker
    ON card_dependency_wait (tenant_id, blocker_card_id)
    WHERE resolved_at_utc IS NULL;
