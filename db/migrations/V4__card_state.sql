create table if not exists card_state (
    card_id           text        primary key,
    last_processed_event text     null,
    current_lock      text        null,
    last_known_list   text        null,
    waiting_on_human  boolean     not null default false,
    updated_at_utc    timestamptz not null default now()
);
