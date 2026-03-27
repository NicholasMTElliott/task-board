Perform a security threat model review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on security threats and trust boundary concerns at the design level:

1. **Attack surface**: What new attack vectors does this design introduce?
2. **Trust boundaries**: Where does data cross trust boundaries? Are those crossings secured?
3. **Data classification**: Is sensitive data identified? Is it handled with appropriate controls?
4. **Authentication design**: Is the authentication mechanism appropriate for the threat model?
5. **Authorization design**: Is access control enforced at the right layer? Can privilege escalation occur?
6. **Cryptography**: If cryptography is involved, are appropriate algorithms and key management planned?
7. **Third-party risk**: Do external integrations introduce supply chain or data exposure risks?
8. **STRIDE analysis**: Spoofing, Tampering, Repudiation, Information Disclosure, Denial of Service, Elevation of Privilege.

## Instructions

- Read the design document in the workspace.
- For each threat, specify the attack vector, potential impact, and recommended mitigation.
- Classify threats by severity: Critical / High / Medium / Low.
- If no security threats found, return COMPLETE with a brief summary of what was assessed.
