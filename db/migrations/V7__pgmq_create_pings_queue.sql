-- Lightweight "ping" queue for webhook notifications.
-- Messages are minimal (source + timestamp); the consumer re-queries the board on receipt.
select pgmq.create('pings');
