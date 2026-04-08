# Product Context

## Why This Exists
Manual ticket management is repetitive: drafting technical designs, generating test plans, and pushing cards through pipeline states all require human time on low-creativity work. This system delegates that toil to specialized AI agents while keeping the human in control of review and approval decisions.

## Problems It Solves
- Technical designs are missing or informal → Senior Engineer agent produces structured plans
- QA coverage is ad-hoc or forgotten → QA agent validates implementations systematically
- Pipeline stalls because a human forgot to push a card → agents drive state transitions automatically
- AI output cannot be trusted blindly → mandatory manual review gates + automated gate checks ensure oversight
- Cross-ticket conflicts go unnoticed → dedicated review steps assess related tickets before and after design
- Ticket sizing is inconsistent or skipped → estimator agent produces calibration-based size estimates
- User stories lack task decomposition → agents can generate child task tickets automatically
- Agent output is text-only, limiting richness of designs and bug reports → agents can write images to `.aiboard/images/output/` and the harness uploads them to GitHub so they appear inline in tickets

## How It Should Work (Operator Perspective)
1. Operator creates an issue and adds it to the project board in **Backlog**.
2. Operator writes requirements/scope/context and moves card to **Ready for Design**.
3. Operator runs: `.\scripts\run_once.ps1 -CardId {N}` (or uses `--mode polling` for automatic pickup)
4. Design runs 4 steps: review related tickets → create technical design → review for cross-ticket conflicts → estimate ticket size. Gate check validates output and may trigger optional specialist reviews (security, performance, etc.). Card moves to **Designed** with estimate written to board field. If the card has a parent story, the story's estimate is recalculated as the sum of its children.
5. Operator reviews design. **For user stories** (`type:story`), approves by moving to **Ready for Tasking**. **For tasks/bugs**, approves by moving to **Ready for Implementation**.
6. *(Stories only)* Tasking decomposes the story into child tasks. Each child inherits the story's priority, gets a best-guess estimate, and is placed in **Ready for Design**. The story's estimate is set to the sum of child estimates. Story moves to **Waiting for Tasks** and completes automatically when all children reach **Done**.
7. Implementation runs 2 steps: implement code (Sonnet 4.6) → code review (Sonnet 4.6). Gate check validates output and may trigger optional specialist reviews. Card moves to **Ready for Test**.
8. Test runs 2 steps: QA agent validates implementation → documentation agent updates memory bank if implementation introduced new patterns/components. Gate check validates output (including doc updates) and may trigger optional specialist reviews. Moves to **Tested** on success.
9. Operator approves by moving to **Approved**. System auto-merges the PR and moves to **Done**.

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
