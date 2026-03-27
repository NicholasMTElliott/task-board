Perform a security testing review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on the adequacy of security-related test coverage:

1. **Authentication tests**: Are authentication flows tested, including invalid credentials and token expiry?
2. **Authorization boundary tests**: Are access control rules tested for both authorized and unauthorized users?
3. **Input validation tests**: Are injection attacks (SQL, XSS, path traversal) tested with malicious inputs?
4. **Sensitive data exposure tests**: Are tests verifying sensitive data is not leaked in responses or logs?
5. **Rate limiting tests**: Are brute-force and flooding scenarios tested?
6. **Security regression tests**: For known vulnerabilities, are regression tests present?

## Instructions

- Read the test files and implementation in the workspace.
- For each gap, cite the security concern that lacks test coverage and its risk.
- If security test coverage is adequate, return COMPLETE with a summary of what was verified.
