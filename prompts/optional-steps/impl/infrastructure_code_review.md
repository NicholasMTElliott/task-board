Perform an infrastructure code review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on infrastructure-as-code, deployment scripts, and environment management:

1. **IaC correctness**: Are Terraform, CDK, or other IaC definitions syntactically and semantically correct?
2. **Security in IaC**: Are IAM roles appropriately scoped? Are security groups restrictive?
3. **Dockerfile and container config**: Are images minimal, non-root, and free of sensitive data?
4. **CI/CD pipeline changes**: Are workflow files secure? Are secrets handled correctly?
5. **Environment variable management**: Are environment-specific values parameterized? No hardcoded values?
6. **Idempotency**: Can the infrastructure changes be applied multiple times safely?
7. **Drift detection**: Are there any manual changes that won't be captured in code?

## Instructions

- Read all infrastructure files changed in the workspace.
- For each finding, cite the file path, line number, and specific concern.
- If no infrastructure concerns found, return COMPLETE with a brief summary of what was verified.
