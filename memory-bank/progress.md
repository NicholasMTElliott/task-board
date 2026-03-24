# Progress

## Completed
- Generic `ITaskBoardClient` abstraction replacing Trello-specific interface
- `GitHubProjectsClient` implementation (via `gh` CLI + GraphQL)
- `StubTaskBoardClient` for testing
- IN_PROGRESS transition in `AgentRunner` (moves card to "X-ing" before agent runs)
- `workflow.github.json` with full GitHub Projects column mapping
- GitHub Project board setup: 11 columns with correct names + Error column
- End-to-end validation: CLI → GitHub Projects → stub agent → card moved + comment posted
- `TaskFileManager` and `AgentRunner` migrated from `TrelloCard` to `BoardCard`
- Unit tests for IN_PROGRESS transitions (13 passing)
- Integration tests for agent execution with Claude CLI (passing)

## Current State
- Direct CLI agent mode is the primary execution path
- GitHub Projects is the active board provider
- Queue-based webhook flow (Orchestrator/EventProcessor) is preserved but secondary
- `ITrelloClient` marked `[Obsolete]`, kept for legacy Orchestrator backward compatibility

## Next Steps
- Run real agent (not stub) against GitHub Project cards
- Test NEEDS_INFO and ERROR outcome paths end-to-end
- Consider automating CLI invocation (webhooks, polling, or manual trigger improvements)
- Clean up legacy Orchestrator to use `ITaskBoardClient` (remove `ITrelloClient` dependency)
