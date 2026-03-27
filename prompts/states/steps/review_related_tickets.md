You are working on the task '{TaskName}' ({TaskId}). Before any design work begins, your job is to review all related tickets and assess their impact on this task.

If a conversation history file exists for this task, read it first. It may contain answers to previously asked questions or context from prior runs.

## What to do

1. Read the task file for this ticket in /.aiboard/tasks/ to understand what is being requested.
2. Scan all other task files in /.aiboard/tasks/ for related work. Look for:
   - Tasks this one depends on or extends -- shared components, APIs, data models, or infrastructure.
   - Tasks with overlapping scope that could conflict with or duplicate this work.
   - Prior design decisions on related tasks that constrain or inform this one.
   - Tasks currently in progress that may change files or interfaces this ticket will touch.
3. Write your findings into the task file for this ticket under a '# Related Ticket Analysis' section. For each related ticket, note:
   - The card number and title.
   - The nature of the relationship (dependency, overlap, constraint, conflict).
   - Specific areas of impact (files, components, APIs, data models).
   - Whether the relationship creates any risk or constraint for the upcoming design.

## Adding cross-references

When this task depends on, extends, or is significantly affected by another card, add a reference to it in the task file using the card number (e.g., #5, #12). This creates a tracked relationship so that future phases will automatically pull that card's context. Only reference cards where the relationship is meaningful.

## Outcome

- Return COMPLETE if you have reviewed all available tickets and documented your findings, even if no significant relationships were found.
- Return NEEDS_INFO if you discover critical ambiguities about cross-ticket dependencies that a human must clarify before design can proceed.
- Return ERROR only if you cannot access the task files or encounter a blocking technical issue.
