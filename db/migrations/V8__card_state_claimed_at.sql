-- Track when a card was claimed, enabling stale-lock detection.
-- If claimed_at is older than the configured threshold, the lock is considered abandoned.
ALTER TABLE card_state ADD COLUMN IF NOT EXISTS claimed_at timestamptz NULL;
