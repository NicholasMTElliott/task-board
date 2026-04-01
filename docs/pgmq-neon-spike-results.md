# PGMQ on Neon Spike Results

> **Legacy Prototype** — This documents the Trello/webhook/PGMQ/Lambda prototype path. The current primary workflow uses GitHub Projects with direct CLI and polling mode. See the project README for current architecture.

## Goal
Validate whether Neon supports the PGMQ SQL/function path required for queue operations.

## Current Status
✅ Completed successfully.

`db/sql/0002_pgmq_create.sql` was applied and validated on Neon.

## Test Checklist

- [x] SQL-only PGMQ install script executes on Neon
- [x] `pgmq.create('events')` succeeds
- [x] `pgmq.send()` inserts a test message
- [x] `pgmq.read()` claims the message with visibility timeout
- [x] `pgmq.delete()` acknowledges processed message
- [x] `pgmq.archive()` works for audit retention path

## Pass Criteria
All checklist items pass consistently across repeated runs.

## Outcome
PGMQ is confirmed viable on Neon for this prototype path when installed via SQL-only script.

## Failure Criteria / Fallback
Any blocker or instability should trigger fallback to `db/sql/0003_jobs_fallback.sql`.
