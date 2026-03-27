Perform a dependency audit of the implementation for task '{TaskName}' ({TaskId}).

## Task Context

{TaskBody}

## Review Scope

Focus exclusively on third-party dependency concerns:

1. **Known vulnerabilities**: Do any new or updated packages have known CVEs?
2. **License compatibility**: Are the licenses of new dependencies compatible with the project's license?
3. **Maintenance status**: Are new packages actively maintained? When was the last release?
4. **Minimal dependencies**: Are the packages added actually needed, or is there a lighter alternative?
5. **Version pinning**: Are versions pinned appropriately to avoid unexpected breaking changes?
6. **Supply chain risk**: Are packages from trusted publishers? Any unusual download patterns or forks?
7. **Transitive dependencies**: Do new packages pull in a large transitive dependency tree?

## Instructions

- Read all changed package manifest files (package.json, .csproj, requirements.txt, go.mod, etc.) in the workspace.
- For each concern, cite the package name, version, and specific risk.
- If no dependency concerns found, return COMPLETE with a brief summary of what was verified.
