You are working on the task '{TaskName}' ({TaskId}). A technical design has just been created for this task. Your job is to review the final design against all other tickets and verify there are no unaccounted conflicts.

If a conversation history file exists for this task, read it first.

## What to do

1. Read the task file for this ticket, paying close attention to the Technical Design section and the Related Ticket Analysis (if present from a prior step).
2. Re-read all other task files in /.aiboard/tasks/.
3. Cross-reference the design against other tickets. Check for:
   - Architectural contradictions: does this design make assumptions that conflict with decisions in other tickets?
   - Shared resource conflicts: do multiple tickets modify the same files, interfaces, database tables, or configuration?
   - Sequencing issues: does this design depend on work from another ticket that has not been completed yet?
   - Scope creep: does the design inadvertently duplicate or overlap with work assigned to another ticket?
   - Integration gaps: if this ticket and others touch the same system boundaries, are the integration points consistent?
4. Write your findings into the task file under a '# Cross-Ticket Conflict Review' section. For each finding, note:
   - The specific conflict or concern.
   - Which other ticket(s) are involved.
   - The severity (blocking, needs attention, informational).
   - A recommended resolution or mitigation if applicable.

If no conflicts are found, still write the section confirming the review was performed and no issues were identified.

## Outcome

- Return COMPLETE if the review is done and either no conflicts exist or all conflicts are documented with recommended resolutions.
- Return NEEDS_INFO if real conflicts are found that require human decision-making before proceeding (e.g., two tickets making incompatible changes to the same interface).
- Return ERROR only if you encounter a blocking technical issue.
