create table if not exists jobs (
    job_id bigserial primary key,
    action_id text not null,
    card_id text null,
    payload_json jsonb not null,
    status text not null default 'pending',
    available_at_utc timestamptz not null default now(),
    claimed_at_utc timestamptz null,
    claimed_by text null,
    attempts integer not null default 0,
    last_error text null,
    created_at_utc timestamptz not null default now(),
    constraint jobs_status_valid check (status in ('pending', 'claimed', 'succeeded', 'failed'))
);

create index if not exists ix_jobs_pending
    on jobs (status, available_at_utc, job_id)
    where status = 'pending';

create index if not exists ix_jobs_action_id
    on jobs (action_id);

-- Claim query pattern for worker implementation (run inside transaction):
--
-- with candidates as (
--   select job_id
--   from jobs
--   where status = 'pending'
--     and available_at_utc <= now()
--   order by job_id
--   for update skip locked
--   limit 10
-- )
-- update jobs j
-- set status = 'claimed',
--     claimed_at_utc = now(),
--     claimed_by = 'lambda-drain',
--     attempts = attempts + 1
-- from candidates c
-- where j.job_id = c.job_id
-- returning j.*;
