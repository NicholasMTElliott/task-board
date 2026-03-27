Perform a compliance verification test review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on compliance-related test coverage:

1. **Audit log completeness tests**: Are tests verifying that all required actions produce audit log entries with correct fields?
2. **Data retention tests**: Are tests verifying that data is retained for the required period and deleted after?
3. **Consent flow tests**: Are tests verifying consent is recorded before data processing begins?
4. **Access control tests**: Are authorization checks tested against the compliance requirements (not just the implementation)?
5. **Regulatory requirement traceability**: Can each compliance requirement be traced to a specific test?
6. **Reporting tests**: If compliance reports are generated, are they tested for accuracy and completeness?

## Instructions

- Read the test files and compliance-related implementation in the workspace.
- For each gap, cite the specific compliance requirement lacking test coverage and its regulatory source.
- If compliance test coverage is adequate, return COMPLETE with a summary of what was verified.
