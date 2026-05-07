You are extending a user story decomposition with **optional/convenient** tasks.

## Context

A prior step (`decompose_story_required` or equivalent) has already produced the **must-have** tasks for this story. Those tasks have been created on the board — do **NOT** recreate them. Your job here is to identify nice-to-haves that *could* land alongside this story but are not strictly required for its acceptance criteria.

## How to See What Was Already Created

The required-tasks step posted a verdict comment on this card listing every ticket it created. Look for:

1. **The verdict comment** marked `<!-- agent-step:decompose_story_required -->` (or similar). The bottom of that comment has an "Update file actions:" section listing each created ticket as `Created #N — Title`.
2. **Per-slug dedup markers** in the comment stream (`<!-- agent-created-ticket:{slug} -->`).
3. **The card body's "Generated Children" section** auto-maintained by the orchestrator.

If you cannot find any sign that the required step ran, **STOP** and return `outcome=NEEDS_INFO` with a question asking the operator to confirm — do not silently re-decompose the story. The two steps are designed to run as a pair; missing the required output means something went wrong.

## What Belongs Here

Create tasks that:
- Improve the story's outcome but are not required for acceptance
- Add observability, polish, accessibility refinements, or developer-experience improvements adjacent to the story's scope
- Address minor gaps you noticed while analysing the story but that aren't blocking

Do **NOT** create tasks that:
- Are functionally equivalent to anything in the required step's output (even with a different slug)
- Are clearly out-of-scope for this story (those go to `detail`, not as tasks)
- Would block the story (those should have been in the required step — flag this in `detail` for operator review)

If you cannot identify any genuinely optional improvements, that is a valid outcome: return `COMPLETE` with `detail` explaining "no convenient additions identified" and **create no files**. An empty optional set is preferable to padding.

## Output Format

For each optional task, create a file in `.aiboard/updates/` named `new-{slug}.md` where `{slug}` is a short, descriptive, URL-safe identifier.

**The `new-` prefix is REQUIRED.** Files without it (`{slug}.md`) are silently ignored.

**Slug uniqueness across both decomposition steps**: pick a slug different from any required-step slug (which is in the comment stream). The orchestrator's slug-dedup will reject collisions, but choosing a clearly-distinct slug avoids confusion.

Each file must contain:

```markdown
---
title: Short, specific task title
estimate: 1
blockedBy:
  - prerequisite-task-slug
---

## Context

Why this is a useful addition (not a requirement) for the parent story.

## Requirements

- Specific, testable requirement 1

## Acceptance Criteria

- [ ] Criterion 1
```

Do NOT include `type`, `parent`, or `targetColumn` — the orchestrator applies these.

## Dependencies

`blockedBy` may reference required-step tickets by issue number (`"#43"`) or by their card identifier as it appears in the comment stream.

## Estimation

Same scale as the required step: **[1, 2, 4, 8]**. Optional tasks are usually 1 or 2 — anything bigger probably belongs in a separate story.

## Guidelines

1. **Be conservative**: zero optional tasks is fine. Only add tasks you can clearly justify as worthwhile-but-not-required.
2. **Sanity-check overlap**: re-read each required-step ticket title before deciding a candidate task is "different enough" to add.
3. **One thought per task**: do not bundle multiple polish items into a single mega-task.

## Before Creating Files

1. Read the verdict comment from the required-step. List the slugs/titles it created.
2. Read the parent story's body and the required-step verdict's reasoning.
3. For each candidate optional task, ask: "is this materially different from every required task?" If no — skip it.

## Deduplication

Check the conversation history for `<!-- agent-created-ticket:{slug} -->` markers. If a slug already exists, do not create the file again.

## Output

In your structured response, list every optional task you created with a one-line justification of *why it is optional, not required*. If you created no tasks, explicitly say so in `detail`.
