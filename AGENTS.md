# Your approach (CRITICALLY IMPORTANT)

It is always acceptable to reply that you don't know the answer to something. You may always ask clarifying questions, prompt for more information, or perform additional research to become more confident in a response or task. You may push back and request confirmation if something doesn't make sense, isn't correct, or isn't the right course of action. Be critical, skeptical, and cautious, but ultimately perform the task requested and don't be a roadblock, just make sure it is the best quality it can be.

Use short sentences. No filler, preamble, or pleasantries. Run tools first, show the result, then stop. Do not narrate unless the situation is exceptional.

Before answering any question, reason step by step. Many questions contain subtle constraints, hidden assumptions, or trick aspects that are invisible to surface-level pattern matching. Verify that the answer you are about to give is actually sensible given ALL the details in the question, not just the most salient one.

# Audience-Split Documentation Architecture

Documentation in this project is split by audience. This split is intentional and must be preserved when adding or updating content.

## `memory-bank/` Memory Bank (Token-Optimized Specification)

After each memory reset, I have zero context. The Memory Bank is my **only** persistent context, inlined below via @ imports. It must be **precise, minimal, unambiguous, and optimized for LLM consumption** — its accuracy directly determines my effectiveness.

### Writing principles

- **Token-efficient.** No filler, no narrative warmth, no redundancy. Dense structured data wins over prose.
- **Machine-parseable format.** Tables, bullet lists, key:value pairs, short declarative sentences. Avoid paragraph walls.
- **Declarative, not narrative.** State what IS, not the journey of how it came to be. No "we originally tried X, but then..." — just the current facts.
- **Terse domain vocabulary.** Use project-specific terms without re-explaining them. Assume the reader is an LLM that can parse compact references.
- **Current state only.** Do not keep change history. When something changes, update the Memory Bank; don't append a timeline.
- **Cross-references over duplication.** If a detail lives in code or another doc, point to it (`see docs/DesignSystem.md`) rather than copying.

### Core Files

```
projectBrief.md     — purpose, requirements, scope
 ├─ productContext.md   — problem → solution, functional intent, UX expectations
 ├─ systemPatterns.md   — architecture, major design choices, component relationships, critical flows
 └─ techContext.md      — tech stack, constraints, setup, dependencies, tooling patterns
```

Add further files under `memory-bank/` for complex features, integrations, APIs, testing, or deployment notes — keep each LLM-optimized.

### Core Workflows

- **Plan Mode** (Claude Code's native plan mode) — design strategy, write to plan file only. Do not edit code.
- **Act Mode** (default) — execute the task; update Memory Bank or `docs/` if project understanding changed.

## `docs/` — Human-Targeted (users and contributors read this)

The `docs/` folder contains **narrative, pedagogical documentation for humans**. These files introduce concepts, explain *why*, and provide guided walkthroughs.

### Writing principles

- **Introduce before using.** If a concept is unfamiliar, explain it first. Don't assume domain vocabulary.
- **Structured narrative.** Use headings, examples, analogies. Paragraphs are fine. Tell a story about the system.
- **Explain rationale.** "Why did we choose X?" matters here (unlike memory-bank). Design decisions and tradeoffs belong in docs.
- **Worked examples welcome.** Code snippets, concrete use cases, step-by-step walkthroughs. Prefer referencing examples in existing code over copying snippets into code and creating duplication.
- **Prescriptive when appropriate.** Design system and contribution guides can and should say "do this, not that."

## `README.md` — Project Entry Point

Top-level `README.md` is the human-facing entry point. It has a Documentation Index linking to `docs/*.md`. When new `docs/` files are created, ALWAYS update the README index with a reference and a brief summary of the contents.

# Task-Based Discovery Paths

When you pick up a task, explore `memory-bank/` for relevant details. Use the root `README.md` to identify which `docs/` files to read for depth or context. Do not automatically consume all of `docs/` unless instructed or the work spans many subsystems.

# Update Rules

Update documentation when new patterns emerge, significant changes occur, context becomes ambiguous, or the user triggers **update memory bank**.

Routing:
- **Memory Bank (`memory-bank/`)** — new architectural patterns → `systemPatterns.md`; tech/stack changes → `techContext.md`; product or UX changes → `productContext.md`; scope changes → `projectBrief.md`. Always delete obsolete content rather than appending.
- **Human Docs (`docs/`)** — new feature with non-trivial scope → new `docs/FeatureName.md` + link from `README.md`; change to existing feature → update the corresponding narrative.

When **update memory bank** is invoked, re-read and reassess **every** Memory Bank file (`ReviewAllFiles → RecordCurrentState → ClarifyNextSteps → CaptureInsights`).

# Memory Precedence
The Memory Bank files (inlined below) are the authoritative project context. They take precedence over Claude Code's auto memory (`~/.claude/projects/.../memory/`). If the two conflict, trust the Memory Bank and update auto memory to resolve.

@README.md
@memory-bank/projectBrief.md
@memory-bank/productContext.md
@memory-bank/systemPatterns.md
@memory-bank/techContext.md
