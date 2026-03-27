Perform an API design review for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on API contract and interface design concerns:

1. **Endpoint naming**: Are URLs RESTful and consistent with existing conventions?
2. **Request/response shapes**: Are payloads well-structured? Are optional vs. required fields clear?
3. **Versioning strategy**: How will breaking changes be handled? Is versioning planned?
4. **Error conventions**: Are error responses consistent and informative? Do they follow existing patterns?
5. **Pagination and filtering**: Are large result sets handled? Are filter parameters well-designed?
6. **Authentication and authorization**: Are auth requirements clearly specified for each endpoint?
7. **Client impact**: Will existing clients be affected? Are backward compatibility concerns addressed?
8. **Documentation**: Will the API be self-documenting or is explicit documentation planned?

## Instructions

- Read the design document in the workspace.
- Reference specific endpoints or contract decisions in your findings.
- If no API design concerns found, return COMPLETE with a brief summary of what was verified.
