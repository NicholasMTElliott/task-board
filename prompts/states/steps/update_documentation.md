You are working on the task '{TaskName}' ({TaskId}). QA testing has passed. Your job is to update the project's memory bank documentation to reflect any changes made during implementation.

If a conversation history file exists for this task, read it first.

## What to do

1. Read the task file to understand what was implemented and how.
2. Review the code changes in the worktree (modified/added/deleted files).
3. Read each memory bank file in `memory-bank/`:
   - `memory-bank/projectBrief.md` — project baseline, scope, and goals
   - `memory-bank/productContext.md` — problem/solution context, operator workflow, UX decisions
   - `memory-bank/systemPatterns.md` — architecture, components, design patterns, workflow config
   - `memory-bank/techContext.md` — tech stack, constraints, dependencies, tooling
4. For each file, determine if the implementation introduced changes that make the documentation stale:
   - New components, patterns, or architectural decisions → update `systemPatterns.md`
   - New dependencies, tooling, or tech stack changes → update `techContext.md`
   - New product capabilities or UX changes → update `productContext.md`
   - Scope changes to the project baseline → update `projectBrief.md` (rare)
5. Update only the files that are genuinely stale. Do not rewrite sections that are already accurate. Add new content or update existing sections as needed.
6. Do NOT remove existing documentation unless it is factually incorrect due to the implementation changes.
7. Also update the `Readme.md` if the implementation changes anything user-facing: roles, steps, workflow config, architecture, or board structure.

## What NOT to do

- Do not update documentation speculatively for things that might change in the future.
- Do not add task-specific details (ticket numbers, implementation dates). The memory bank is a living reference, not a changelog.
- Do not restructure or reorganize files unless the existing structure cannot accommodate the new information.
- Do not make cosmetic or stylistic changes to documentation that is already accurate.

## Outcome

- Return COMPLETE if you reviewed all memory bank files and updated any that were stale (or confirmed all are current).
- Return NEEDS_INFO if you cannot determine what changed (e.g., no task file, no code changes visible).
- Return ERROR only if you encounter a blocking technical issue.
