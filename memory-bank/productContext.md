# Product Context

## Why This Exists
Manual ticket management in Trello is repetitive: writing requirements, drafting technical designs, generating test plans, and pushing cards through pipeline states all require human time on low-creativity work. This system delegates that toil to specialized AI agents while keeping the human in control of review and approval decisions.

## Problems It Solves
- Requirements are under-specified or inconsistent → BA agent extracts and clarifies
- Technical designs are missing or informal → Senior Engineer agent produces structured plans
- QA coverage is ad-hoc or forgotten → QA agent generates test plans systematically
- Pipeline stalls because a human forgot to push a card → agents drive state transitions automatically
- AI output cannot be trusted blindly → mandatory manual review gates ensure human oversight

## How It Should Work (Operator Perspective)
1. Operator creates a card in Backlog with a brief description of the ticket.
2. Operator moves card to **Requirements** — this is the only manual trigger to start automation.
3. BA agent runs, populates the card description sections (Requirements, Open Questions, Acceptance Criteria), and posts a summary comment.
4. Card is moved to **Requirements Review**. Operator reads and approves by moving to **Design**.
5. Senior Engineer agent runs, writes Technical Design section, identifies edge cases.
6. Card is moved to **Design Review**. Operator approves by moving to **Implementation**.
7. Senior Engineer agent produces an implementation breakdown.
8. Card is moved to **Code Review**. Operator performs code review manually, moves to **Testing** when satisfied.
9. QA agent generates a test plan.
10. Card moves to **Done**.

At any agent state, if the agent needs more information:
- Card is moved to a **Questions** holding state (sidebar, not main pipeline).
- Agent posts questions as a Trello comment.
- Operator answers in the comment.
- Operator moves card back to the originating state to re-trigger.

## User Experience Goals
- Trello is the **only** interface the operator needs to interact with.
- All planning content lives on the Trello card — no context scattered across tools.
- Each agent run produces a single updated summary comment (no spam).
- Approval is as simple as dragging a card to the next list.
- System cost is effectively $0 when no tickets are moving.
