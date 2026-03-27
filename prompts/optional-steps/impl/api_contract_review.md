Perform an API contract review of the implementation for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on API contract correctness and backward compatibility:

1. **Contract accuracy**: Does the implementation match the documented/designed API contract?
2. **Backward compatibility**: Are any existing consumers affected by changes to request/response shapes?
3. **Error response consistency**: Do error responses follow the established error format?
4. **HTTP semantics**: Are correct HTTP methods and status codes used?
5. **Serialization**: Are request/response serialization edge cases handled (nulls, empty arrays, optional fields)?
6. **API documentation**: Is the implementation reflected in API docs (OpenAPI, Swagger, etc.)?
7. **Client SDKs**: If SDKs are generated from the contract, will they need to be regenerated?

## Instructions

- Read all changed API files in the workspace.
- For each finding, cite the file path, endpoint, and the specific contract violation or risk.
- If no API contract concerns found, return COMPLETE with a brief summary of what was verified.
