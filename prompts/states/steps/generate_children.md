You are decomposing a user story into implementation tasks.

## Your Goal

Analyze the parent card (the current ticket) and break it down into discrete, independently implementable tasks. Each task should be small enough to be completed in a single implementation cycle.

The orchestrator will automatically apply the type label, parent link, and target column to each created card — you only need to provide the title and body.

## Output Format

For each task, create a file in `.aiboard/updates/` named `new-{slug}.md` where `{slug}` is a short, descriptive, URL-safe identifier (lowercase, hyphens, no spaces).

Each file must contain:

```markdown
---
title: Short, specific task title
---

## Context

Brief description of why this task exists and its relationship to the parent story.

## Requirements

- Specific, testable requirement 1
- Specific, testable requirement 2

## Acceptance Criteria

- [ ] Criterion 1
- [ ] Criterion 2
```

Do NOT include `type`, `parent`, or `targetColumn` in the front matter — the orchestrator applies these from configuration.

## Guidelines

1. **Atomic tasks**: Each task should represent ONE logical unit of work. If a task has "and" in its description, consider splitting it.
2. **Self-contained**: Each task should be implementable without waiting for other tasks (where possible). Note dependencies explicitly if they exist.
3. **Testable**: Every task must have clear acceptance criteria that can be verified by automated tests or manual review.
4. **No duplication**: Do not create tasks for work already covered by existing tickets on the board. Check the task files in `.aiboard/tasks/` for existing work.
5. **Reasonable scope**: Aim for 3–8 tasks per story. Fewer than 3 suggests the story is already task-sized. More than 8 suggests the story should be split into smaller stories first.

## Before Creating Files

1. Read the parent card's full description and requirements.
2. Review existing task files in `.aiboard/tasks/` to avoid duplicating existing work.
3. Check the conversation history for any prior decomposition attempts or reviewer feedback.
4. Design the decomposition — identify the logical units of work, their dependencies, and their acceptance criteria.

## Deduplication

Before creating a `new-{slug}.md` file, check the conversation history for `agent-created-ticket:{slug}` markers. If a ticket with that slug has already been created, do not create the file again.
