Perform an operational cost analysis review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on cost implications of the proposed design:

1. **Compute costs**: What are the estimated compute requirements? How do costs scale with usage?
2. **Storage costs**: What storage is required? How does it grow over time?
3. **Network and bandwidth**: Are there data transfer costs (egress, CDN, inter-region)?
4. **Third-party service costs**: Are new external APIs, SaaS tools, or managed services introduced?
5. **Scaling economics**: At what scale does the cost model break down? Are there cliff edges?
6. **LLM/AI costs**: If AI inference is involved, are token costs and rates estimated?
7. **Cost optimization opportunities**: Are there design choices that could reduce cost without changing functionality?

## Instructions

- Read the design document in the workspace.
- Provide rough estimates where possible (e.g., "~$X/month at Y requests/day").
- Flag any design decisions that create unexpected or unbounded cost growth.
- If no significant cost concerns found, return COMPLETE with a brief cost summary.
