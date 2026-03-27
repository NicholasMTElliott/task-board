Perform a SOC 2 compliance audit of the implementation for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on SOC 2 Trust Service Criteria compliance in the code changes:

1. **Audit logging completeness**: Are all significant actions (data access, mutations, auth events)
   written to an immutable audit log with user, timestamp, and action detail?
2. **Data retention policies**: Are retention periods enforced in code? Is deletion/archival
   automated and verifiable?
3. **Access control enforcement**: Is role-based or attribute-based access control correctly
   implemented? Are permission checks present at every protected operation?
4. **Encryption at rest and in transit**: Is sensitive data encrypted at rest? Is TLS enforced
   for all data transmission?
5. **Change management documentation**: Are changes traceable? Is there evidence of review?
6. **Availability controls**: Are timeouts, retries, and circuit breakers implemented for
   external dependencies?
7. **Incident response readiness**: Are sufficient logs and metrics available to reconstruct
   a security incident?

## Instructions

- Read all changed files in the workspace.
- For each finding, cite the specific SOC 2 criterion (CC6, CC7, CC8, etc.) and the file/line.
- Classify: Non-compliant (must fix) / Gap (should address) / Observation (document).
- If compliant, return COMPLETE with a summary of what was verified and which criteria were assessed.
