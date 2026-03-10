create table if not exists run_log (
    run_id       uuid         primary key default gen_random_uuid(),
    card_id      text         not null,
    role         text         not null,
    input_hash   text         null,
    output_hash  text         null,
    outcome      text         not null,
    created_at_utc timestamptz not null default now(),

    constraint run_log_outcome_valid
        check (outcome in ('COMPLETE', 'NEEDS_INFO', 'BLOCKED', 'ERROR'))
);

create index idx_run_log_card_id on run_log (card_id);
