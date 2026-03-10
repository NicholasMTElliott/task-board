# Active Context

## Current Focus
**Architecture hardening and orchestration prep.** Critical issues from architecture review resolved; system is now ready for orchestration pipeline implementation.

## What Has Been Done
- All prior scaffolding work (see progress.md for full history)
- **Architecture review completed — critical fixes applied:**
  - **Trello webhook HMAC-SHA1 validation:** Replaced incorrect custom-header check with proper HMAC-SHA1 signature verification using Web Crypto API. Worker now validates `x-trello-webhook` header against `(body + callbackURL)` signed with API secret. Added constant-time comparison. Added HEAD request handling for Trello webhook URL verification.
  - **Env var rename:** `TRELLO_WEBHOOK_SECRET` → `TRELLO_API_SECRET` + new `TRELLO_WEBHOOK_CALLBACK_URL`. Updated `.env.example`, `local-secrets.ps1`, `wrangler.toml`.
  - **Non-card event filtering:** Worker now returns 200 with `accepted: false` for events without `action.data.card` (prevents 500s from board/list events that could cause Trello to disable the webhook).
  - **card_state and run_log migrations:** Added `V4__card_state.sql` and `V5__run_log.sql` to Flyway chain. Tables match systemPatterns.md schema.
  - **workflow.v1.json created:** File-based workflow config with all 10 states (9 pipeline + Questions), 3 agent roles with system prompts and section assignments. List IDs are placeholders pending Trello board setup.
  - **Archive-before-delete fix:** `QueueRepository.MarkSucceededAsync` now archives first (preserving audit trail), falls back to delete only if archive fails, with warning log.
  - **Interface extraction:** Added `IQueueRepository` and `IProcessedEventsRepository` interfaces. `EventProcessor` now depends on interfaces, enabling unit testing.
  - **Test project added:** `TaskBoard.Worker.Tests` (xUnit + NSubstitute) with 10 tests covering `EventProcessor` batch processing (empty queue, new messages, duplicates, mixed batches, error handling) and `QueueProcessingOptions` validation logic. All passing.

## Next Steps (Priority Order)
1. Set up Trello board with 9 pipeline lists + Questions list, capture list IDs
2. Replace placeholder list IDs in `workflow.v1.json` with real Trello list IDs
3. Implement `ProcessMessageAsync` orchestration pipeline:
   - Workflow config loader
   - State → role resolver (look up card's current list in workflow config)
   - Card description section parser/writer (Trello API)
   - Agent invocation (OpenAI API call with role-specific system prompt)
   - Agent output validation against contract schema
   - Comment upsert (single Agent Status comment)
   - Card list mover (orchestrator-controlled transitions)
4. Implement locking mechanism using `card_state.current_lock`
5. Implement NEEDS_INFO → Questions flow
6. Run Flyway migrations (`V4`, `V5`) against Neon
7. End-to-end smoke test: BA loop (Requirements → Questions → Requirements → Requirements Review)

## Active Decisions Under Consideration
- Lambda deployment model (AOT vs. managed runtime) not decided
- IaC toolchain not yet selected (Terraform vs. SAM vs. CDK)
- Whether sweeper trigger is needed beyond kick-only flow
- Agent output validation strategy (JSON Schema vs. C# validation vs. shared contract type)
- Whether Worker should use `ctx.waitUntil()` for async enqueue to avoid Neon latency on critical path

## Known Risks
- Trello webhook delivery is not guaranteed exactly-once; idempotency is critical
- Cross-cloud operation (Cloudflare + Neon + AWS) adds auth/observability complexity
- Lambda + Neon cold start/resume latency may stack after idle
- Kick endpoint failures can delay processing unless sweeper fallback exists
- `MarkFailedAsync` is still a no-op — failing messages retry indefinitely with no max-retry or dead-letter
- `PGMQ_QUEUE_NAME` still read from environment directly in `QueueRepository` (not via options binding)
