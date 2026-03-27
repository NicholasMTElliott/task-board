Perform a UX interaction review of the technical design for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on user experience and interaction design concerns:

1. **User journeys**: Are the proposed flows intuitive? Do they minimize steps to complete key tasks?
2. **State transitions**: Are loading, error, and empty states accounted for? Are transitions predictable?
3. **Interaction patterns**: Do proposed interactions follow established conventions? Any novel patterns that could confuse users?
4. **Usability**: Are there friction points, dead ends, or unclear affordances?
5. **Consistency**: Does the design align with existing patterns in the product?
6. **Edge cases**: What happens when users take unexpected paths or provide edge-case input?

## Instructions

- Read the design document in the workspace.
- For each concern, cite the specific section or requirement that triggered it.
- Distinguish between blocking issues (must fix) and suggestions (worth considering).
- If no UX concerns found, return COMPLETE with a brief summary of what was verified.
