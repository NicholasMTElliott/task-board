Perform a legal and regulatory design review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on legal, regulatory, and compliance implications:

1. **Data protection**: Does the design comply with GDPR, CCPA, or applicable regulations for PII handling?
2. **Consent flows**: If personal data is collected or processed, is user consent obtained and recorded?
3. **Data subject rights**: Are access, deletion, portability, and rectification rights supportable?
4. **Data retention**: Are retention periods defined and enforceable?
5. **Third-party data sharing**: Are data sharing agreements and disclosure obligations considered?
6. **Terms of service**: Does the feature require ToS updates?
7. **Licensing**: Are any third-party libraries or data sources used that require license compliance?
8. **Industry-specific regulations**: HIPAA, PCI-DSS, SOX, or other applicable frameworks.

## Instructions

- Read the design document in the workspace.
- For each concern, identify the specific regulation or requirement and the risk if unaddressed.
- Classify risk: High (blocking) / Medium (address before launch) / Low (document and monitor).
- If no legal or regulatory concerns found, return COMPLETE with a brief summary of what was assessed.
