You are working on the task '{TaskName}' ({TaskId}). Code has just been implemented for this task. Your job is to perform a thorough code review of the changes.

If a conversation history file exists for this task, read it first.

## What to do

1. Read the task file for this ticket to understand the requirements and technical design.
2. Examine all code changes in the worktree. Review modified, added, and deleted files.
3. Evaluate the implementation against these criteria:

### Correctness
- Does the code implement all requirements from the technical design?
- Are there logic errors, off-by-one mistakes, or unhandled edge cases?
- Do the tests actually prove the requirements are met?

### Code quality
- Does the code follow existing patterns and conventions in the codebase?
- Are names clear and descriptive?
- Is there unnecessary complexity that could be simplified?
- Are there any code smells (duplication, god classes, overly long methods)?

### Test coverage
- Are all new code paths covered by tests?
- Do tests cover both success and failure cases?
- Do tests verify behavior (contract), not implementation details?
- Are the tests following existing test patterns (xUnit, NSubstitute)?

### Safety
- Are there any security concerns (injection, improper validation at system boundaries)?
- Could any changes break existing functionality?
- Are there any race conditions or concurrency issues?

4. Write your review findings into the task file under a '# Code Review' section. For each finding:
   - Describe the issue clearly.
   - Reference the specific file and area of code.
   - Classify as: blocking (must fix), suggestion (should fix), or nit (optional).
   - Provide a recommended fix if applicable.

If the code passes review with no issues, still write the section confirming the review was performed.

## Outcome

- Return COMPLETE if the code passes review (no blocking issues found). Suggestions and nits are acceptable.
- Return NEEDS_INFO if there are blocking issues that require the implementer or a human to address before the code can proceed to testing.
- Return ERROR only if you encounter a blocking technical issue.
