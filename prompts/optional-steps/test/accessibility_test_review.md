Perform an accessibility testing review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on accessibility test coverage:

1. **Automated a11y scans**: Are automated accessibility scanners (axe, WAVE, etc.) integrated into the test suite?
2. **Keyboard navigation tests**: Are keyboard-only usage scenarios tested programmatically?
3. **Screen reader tests**: Are screen reader interaction patterns tested or documented for manual verification?
4. **WCAG checkpoint verification**: Are WCAG 2.1 AA success criteria verified for the changed components?
5. **Focus management tests**: For dynamic content and modals, is focus management tested?
6. **Color contrast verification**: Is contrast ratio verified programmatically or in visual tests?

## Instructions

- Read the test files in the workspace.
- For each gap, cite the specific accessibility scenario lacking coverage and its WCAG criterion.
- If accessibility test coverage is adequate, return COMPLETE with a summary of what was verified.
