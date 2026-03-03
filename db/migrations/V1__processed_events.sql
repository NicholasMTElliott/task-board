create table if not exists processed_events (
    action_id text primary key,
    processed_at_utc timestamptz not null default now()
);
