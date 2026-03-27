Perform a WCAG 2.1 AA accessibility audit of the implementation for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on accessibility compliance in the UI implementation:

1. **Semantic HTML**: Are the correct HTML elements used (nav, main, button, etc.) rather than div/span with roles?
2. **ARIA attributes**: Are ARIA labels, descriptions, and roles present where needed?
3. **Keyboard navigation**: Can all interactive elements be reached and activated via keyboard (Tab, Enter, Space, arrow keys)?
4. **Focus management**: For modals, drawers, and route changes — is focus moved appropriately?
5. **Color contrast**: Do text and interactive elements meet 4.5:1 (normal text) or 3:1 (large text) contrast ratios?
6. **Screen reader compatibility**: Are dynamic content updates announced via aria-live or equivalent?
7. **Form accessibility**: Do all form fields have associated labels? Are error messages programmatically associated?

## Instructions

- Read all changed UI files in the workspace.
- For each finding, cite the file path, element, and specific WCAG success criterion (e.g., 1.3.1, 4.1.2).
- If no accessibility issues found, return COMPLETE with a brief summary of what was verified.
