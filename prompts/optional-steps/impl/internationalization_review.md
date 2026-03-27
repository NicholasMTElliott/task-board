Perform an internationalization (i18n) review of the implementation for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on internationalization and localization readiness:

1. **String externalization**: Are all user-visible strings externalized to locale files? No hardcoded English text?
2. **Date and time formatting**: Are dates and times formatted using locale-aware APIs?
3. **Number and currency formatting**: Are numbers and currency values formatted with locale awareness?
4. **Plural forms**: Are plural forms handled correctly for languages with complex pluralization rules?
5. **RTL support**: If right-to-left languages are planned, is the layout RTL-compatible?
6. **Character encoding**: Is UTF-8 used throughout? Are there any ASCII-only assumptions?
7. **Locale-dependent logic**: Is any business logic incorrectly dependent on locale (string comparisons, sorting)?

## Instructions

- Read all changed files in the workspace.
- For each finding, cite the file path, line, and specific i18n concern.
- If no i18n concerns found, return COMPLETE with a brief summary of what was verified.
