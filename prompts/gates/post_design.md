Verify the technical design produced for task '{TaskName}' ({TaskId}).

## Original Task Requirements

{TaskBody}

## Design Output

{Diff}

## Tickets Created During This Run

{CreatedTickets}

## Agent Self-Report

{AgentReport}

## Verification Checklist

1. Does the design address every requirement listed in the task?
2. Does the design introduce scope not present in the requirements?
3. Are there any obvious gaps (e.g., requirements mentioned but not designed for)?
4. If this state was supposed to produce child tickets (decomposition / generation), does the "Tickets Created" list above match what the agent's self-report claims? An empty list when the self-report claims tickets were made is a real failure — the agent may have written files without the required `new-` prefix or the orchestrator may have rejected them.
