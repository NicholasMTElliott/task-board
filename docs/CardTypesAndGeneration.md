# Card Types and Child Generation

This guide covers the two discriminator mechanisms for identifying a card's type (**labels** and **project fields**) and how `generationConfig` creates child cards with the right metadata to enter their pipeline.

## Why discriminate card types?

Some workflows treat different kinds of cards differently — user stories get decomposed into tasks, bugs skip design, tasks inherit priority from their parent story. The engine uses a card's *type* to:

- Enforce `allowedChildren` when generating children (`Story` may produce `Task`; `Task` may produce nothing).
- Look up parent-type rules from a card's parent.

Two mechanisms are supported; you may use either, or both, on the same board.

## Mechanism 1 — Label-based (default)

A type is represented by a GitHub/Trello label of the form `{labelPrefix}:{typeKey}`.

```json
"cardTypes": {
  "story": { "name": "User Story", "labelPrefix": "type", "allowedChildren": ["task"] },
  "task":  { "name": "Task",       "labelPrefix": "type", "allowedChildren": [] }
}
```

A story card gets the label `type:story`; a task gets `type:task`. Labels are created on-demand the first time they're applied.

### Opting out of labels

Set `labelPrefix` to `null` (omit the key) when you don't want labels for a given type. This is typical when you're fully field-based — see below. The type still needs *some* discriminator: the validator errors if `labelPrefix` is empty **and** no workflow-level `cardTypeField` is set.

## Mechanism 2 — Field-based

Many teams categorize work using a GitHub Projects single-select field (e.g. `Type` with options `Story`, `Task`, `Bug`) rather than labels. Declare this at the workflow level:

```json
"cardTypeField": "Type",
"cardTypes": {
  "story": { "name": "User Story", "allowedChildren": ["task"] },
  "task":  { "name": "Task",       "allowedChildren": [] }
}
```

When `cardTypeField` is set:

- Generated children have `CardTypeDefinition.Name` written to that field on create (`Type = "Task"`).
- Parent-type lookup reads the field from the parent's metadata first (for `allowedChildren` enforcement). If the field is empty or absent, the engine falls back to label matching.
- `CardTypeDefinition.Name` must match an option of the single-select field exactly — the `--mode validation` runner cross-checks this against the live board.

### Combining labels and fields

Both mechanisms can coexist. If `cardTypeField` is set **and** a type also has a non-empty `labelPrefix`, the engine writes both the field and the label to generated children. Parent-type resolution prefers the field but accepts the label as a fallback. This is useful during migration from one scheme to the other, or when different tooling needs different discriminators.

## Setting arbitrary project fields on generated children

`GenerationConfig.setFields` is a literal field→value map applied to every child the step creates:

```json
"generationConfig": {
  "targetType": "task",
  "targetColumn": "Ready",
  "linkToParent": true,
  "copyFields": ["Priority"],
  "setFields": {
    "Type": "Task",
    "Activity": "Design"
  }
}
```

Merge order into the final `CreateCardRequest.FieldValues` (later wins):

1. `copyFields` — values read from the parent card's metadata.
2. Estimate from the `new-*.md` front matter (under `estimation.fieldName`, default `Estimate`).
3. Implicit `cardTypeField` assignment (when configured) — sets the type field to `CardTypeDefinition.Name`.
4. `setFields` — literal values. **Wins on any key collision.**

Typical use: drop children directly into the right pipeline stage by stamping the stage-discriminator field (`Activity`, `Stage`, etc.) so the child is immediately picked up by the corresponding state's filter.

## Adding dependencies when creating tickets

Agents can declare hard sequencing constraints in any `new-*.md` ticket file. This works for structured story decomposition and for incidental tickets created during design, implementation, review, or QA.

```markdown
---
title: Add API endpoints
estimate: 2
blockedBy:
  - create-database
  - "#123"
blocks:
  - follow-up-cleanup
---

## Context

This task depends on the database task.
```

`blockedBy` means the new ticket cannot start until the referenced card is satisfied. `blocks` means the referenced card cannot start until the new ticket is satisfied.

References may be:

- A same-batch slug, such as `create-database`, matching another `new-create-database.md` file from the same step.
- An existing ticket number, such as `#123` or `123`.
- `current`, meaning the source card the agent is working on.

Use dependencies only for hard sequencing. Do not use them for related context, preferred order, or "nice to do first" work.

When `dependencyPolicy.enabled` is true, polling skips blocked cards and selects the next available card. Direct agent and merge runs refuse blocked cards before moving them to an in-progress state.

## Validation

Run `--mode validation --board-id N` to cross-check the config against the live board:

- `cardTypeField` (if set) exists as a project field.
- Each `cardTypes[k].name` is an accepted option of that field.
- Every `setFields` key is a real field; for single-select fields, each value is an accepted option. `{{templated}}` values are skipped.
- Label warnings (`type:foo is not defined on the repo`) are suppressed for types whose `labelPrefix` is empty — the board isn't using labels for those types.

## Migration tips

- Start from labels, add a `cardTypeField`, run a backfill step, then null out `labelPrefix` once all cards have the field set.
- To enable the field-based scheme without touching label configs, set `cardTypeField` and add `setFields: { "Type": "..." }` on your tasking step. Existing label-based cards keep working; newly generated cards get both discriminators until you remove the label path.
