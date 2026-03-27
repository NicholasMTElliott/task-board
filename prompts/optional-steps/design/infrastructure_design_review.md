Perform an infrastructure design review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on infrastructure and deployment concerns:

1. **Hosting and scaling**: Does the design account for expected load? Are scaling strategies defined?
2. **Resource provisioning**: Are compute, storage, and network resources appropriately specified?
3. **Deployment topology**: Is the deployment model (serverless, containers, VMs) appropriate for the workload?
4. **Cost implications**: Are there cost risks from the proposed infrastructure choices?
5. **Operational concerns**: Backup, recovery, maintenance windows, and upgrade paths.
6. **CI/CD pipeline impact**: Does the design require changes to deployment pipelines?
7. **Environment parity**: Are dev, staging, and prod environments sufficiently similar?

## Instructions

- Read the design document in the workspace.
- Reference specific infrastructure decisions in your findings.
- Flag any decisions that could cause operational issues or unexpected costs.
- If no infrastructure concerns found, return COMPLETE with a brief summary of what was verified.
