# Product Context

## Why This Exists
Manual ticket management is repetitive: drafting technical designs, generating test plans, and pushing cards through pipeline states all require human time on low-creativity work. This system delegates that toil to specialized AI agents while keeping the human in control of review and approval decisions.

## Problems It Solves
- Technical designs are missing or informal → Senior Engineer agent produces structured plans
- QA coverage is ad-hoc or forgotten → QA agent validates implementations systematically
- Pipeline stalls because a human forgot to push a card → agents drive state transitions automatically
- AI output cannot be trusted blindly → mandatory manual review gates ensure human oversight

## How It Should Work (Operator Perspective)
1. Operator creates an issue and adds it to the project board in **Backlog**.
2. Operator writes requirements/scope/context and moves card to **Ready for Design**.
3. Operator runs the CLI: `dotnet run -- --mode agent --card-id {N} --board-id 1 --workspace .`
4. Senior Engineer agent moves card to **Designing**, produces Technical Design, moves to **Designed**.
5. Operator reviews design, approves by moving to **Ready for Implementation**.
6. Operator runs CLI again. Senior Engineer agent moves to **Implementing**, writes code, moves to **Ready for Test**.
7. Operator runs CLI again. QA agent validates implementation, moves to **Tested** on success.

At any agent state, if the agent needs more information:
- Card is moved to the relevant **Questions** holding column (Design Questions or Implementation Questions).
- Agent posts questions as a comment on the issue.
- Operator answers and moves card back to the "Ready for" state to re-trigger.

On error:
- Card is moved to **Error** column with error details posted as a comment.

## User Experience Goals
- The kanban board (GitHub Projects, Trello, etc.) is the **only** interface the operator needs.
- All planning content lives on the card/issue — no context scattered across tools.
- Each agent run produces a single updated summary comment (no spam).
- Approval is as simple as dragging a card to the next column.
- Board provider is swappable via `ITaskBoardClient` abstraction (GitHub Projects, Trello, future Jira/Basecamp).
- System cost is effectively $0 when no tickets are moving.
