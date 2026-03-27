Perform a data privacy review of the implementation for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on data privacy practices and compliance with GDPR/CCPA principles:

1. **Data minimization**: Is only the minimum necessary personal data collected and processed?
2. **PII handling**: Is personally identifiable information identified, labeled, and handled with appropriate controls?
3. **Consent enforcement**: If personal data processing requires consent, is consent checked before processing?
4. **Data subject rights**: Are access, deletion, portability, and correction rights supported for new data?
5. **Anonymization and pseudonymization**: Where PII is not strictly needed, is it anonymized or pseudonymized?
6. **Data sharing**: Is personal data shared with third parties? Is this disclosed and authorized?
7. **Breach detection**: Are there mechanisms to detect and alert on unauthorized access to personal data?
8. **Data residency**: Are data residency requirements (e.g., EU data must stay in EU) respected?

## Instructions

- Read all changed files in the workspace.
- For each finding, cite the relevant GDPR/CCPA article or principle, file path, and specific concern.
- Classify: High (blocking compliance risk) / Medium (should address) / Low (document and monitor).
- If no privacy concerns found, return COMPLETE with a brief summary of what was assessed.
