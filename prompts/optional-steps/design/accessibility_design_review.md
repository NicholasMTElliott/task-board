Perform an accessibility design review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on accessibility and inclusive design concerns:

1. **Keyboard navigation**: Can all proposed interactions be completed using keyboard only?
2. **Screen reader support**: Are semantic elements and ARIA roles planned? Is content meaningful without visuals?
3. **Color and contrast**: Is color used as the sole differentiator anywhere? Are contrast ratios planned?
4. **Focus management**: For modals, drawers, and dynamic content — is focus management considered?
5. **Motion and animation**: Are reduced-motion preferences accounted for?
6. **Touch targets**: Are interactive elements appropriately sized for touch devices?
7. **WCAG 2.1 AA compliance**: Which success criteria are relevant to this feature?

## Instructions

- Read the design document in the workspace.
- Reference specific proposed UI elements or interactions in your findings.
- For each concern, cite the relevant WCAG success criterion where applicable.
- If no accessibility concerns found, return COMPLETE with a brief summary of what was verified.
