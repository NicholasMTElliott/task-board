Perform a UI visual implementation review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on visual implementation quality:

1. **Design system adherence**: Does the implementation match the design system tokens (colors, spacing, typography)?
2. **Responsive layout**: Does the layout work across breakpoints? Are there overflow or alignment issues?
3. **Theming**: Does the component respect light/dark mode or other themes?
4. **Spacing and typography**: Are spacing, font sizes, and line heights consistent with the design?
5. **Cross-browser considerations**: Are any CSS properties used that lack broad browser support?
6. **Pixel-perfect**: Does the implementation match the design mockup (if one exists)?
7. **Visual regressions**: Could this change affect the appearance of other components?

## Instructions

- Read all changed style and component files in the workspace.
- For each finding, cite the file path and specific visual concern.
- If no visual concerns found, return COMPLETE with a brief summary of what was verified.
