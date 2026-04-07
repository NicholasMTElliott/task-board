You are estimating the complexity of task '{TaskName}' ({TaskId}).

## Instructions

1. Read the task file for this ticket at `/.aiboard/tasks/` to understand the full design that was just produced.
2. Read the calibration ticket #{CalibrationTicketId} (also in `/.aiboard/tasks/`) to understand its scope and complexity.
3. The calibration ticket has been sized at **{CalibrationSize} story point(s)**.
4. Compare the current task against the calibration ticket across these dimensions:
   - Scope of code changes (files, components, layers)
   - Complexity of logic (algorithms, edge cases, state management)
   - Testing effort (number and type of tests needed)
   - Risk (regressions, breaking changes, unknowns)
   - Dependencies (external systems, cross-cutting concerns)
5. Assign a story point estimate using the scale: **{EstimationScale}**.
6. If the estimate is 16 or higher, explicitly flag that the ticket should be considered for splitting.
7. If the calibration ticket is not available, provide your best absolute estimate based on the design alone.

## Output

- Set the `estimate` field in your response to the numeric story point value.
- In the `detail` field, provide a brief rationale (2-4 sentences) explaining your estimate relative to the calibration ticket.

## Outcome

- Return **COMPLETE** with your estimate and rationale.
- Return **NEEDS_INFO** only if the design is missing or too vague to estimate.
