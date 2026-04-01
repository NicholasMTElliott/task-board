# Product Context

## Why This Exists
Manual ticket management is repetitive: drafting technical designs, generating test plans, and pushing cards through pipeline states all require human time on low-creativity work. This system delegates that toil to specialized AI agents while keeping the human in control of review and approval decisions.

## Problems It Solves
- Technical designs are missing or informal → Senior Engineer agent produces structured plans
- QA coverage is ad-hoc or forgotten → QA agent validates implementations systematically
- Pipeline stalls because a human forgot to push a card → agents drive state transitions automatically
- AI output cannot be trusted blindly → mandatory manual review gates + automated gate checks ensure oversight
- Cross-ticket conflicts go unnoticed → dedicated review steps assess related tickets before and after design

## How It Should Work (Operator Perspective)
1. Operator creates an issue and adds it to the project board in **Backlog**.
2. Operator writes requirements/scope/context and moves card to **Ready for Design**.
3. Operator runs: `.\scripts\run_once.ps1 -CardId {N}` (or uses `--mode polling` for automatic pickup)
4. Design runs 3 steps: review related tickets → create technical design → review for cross-ticket conflicts. Gate check validates output and may trigger optional specialist reviews (security, performance, etc.). Card moves to **Designed**.
5. Operator reviews design, approves by moving to **Ready for Implementation**.
6. Implementation runs 2 steps: implement code (Sonnet 4.6) → code review (Opus 4.6). Gate check validates output and may trigger optional specialist reviews. Card moves to **Ready for Test**.
7. QA agent validates implementation. Gate check validates output and may trigger optional specialist reviews. Moves to **Tested** on success.
8. Operator approves by moving to **Approved**. System auto-merges the PR and moves to **Done**.

At any agent state, if the agent needs more information:
- Card is moved to the relevant **Questions** holding column.
- Agent posts questions as a comment on the issue.
- Operator answers and moves card back to the "Ready for" state to re-trigger.

On error:
- Card is moved to **Error** column with error details posted as a comment.

## User Experience Goals
- The kanban board (GitHub Projects, Trello, etc.) is the **only** interface the operator needs.
- All planning content lives on the card/issue — no context scattered across tools.
- Each step produces its own comment on the card (with unique markers to avoid collision).
- Approval is as simple as dragging a card to the next column.
- Board provider is swappable via `ITaskBoardClient` abstraction.
- System cost is effectively $0 when no tickets are moving.
