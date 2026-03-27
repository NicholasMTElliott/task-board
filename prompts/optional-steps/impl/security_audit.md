Perform a security audit of the implementation for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on security concerns in the code changes:

1. **Input validation**: All user/external input sanitized before use. No raw SQL,
   unescaped HTML, or unsanitized path construction.
2. **Authentication & authorization**: Auth checks present on all protected paths.
   No privilege escalation vectors. Token handling follows best practices.
3. **Data exposure**: No sensitive data in logs, error messages, or API responses.
   PII properly masked. Secrets not hardcoded.
4. **Injection risks**: SQL injection, command injection, XSS, SSRF, path traversal.
5. **Cryptography**: Appropriate algorithms, proper key management, no hardcoded keys.
6. **Dependencies**: Known CVEs in new/updated dependencies.
7. **Error handling**: Errors don't leak internal details. Stack traces not exposed
   to users.

## Instructions

- Read all changed files in the workspace.
- For each finding, cite the file path, line number, and specific vulnerability.
- Classify severity: Critical / High / Medium / Low.
- Recommend a specific fix for each finding.
- If no security issues found, return COMPLETE with a brief summary of what you verified.
