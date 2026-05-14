# Contributing

## Local Setup

Start with `QUICKSTART.md`. For build and sandbox setup, follow the build sections in `README.md` instead of duplicating those steps here.

## Tests

Run the .NET test suite from `lambda/`:

```powershell
dotnet test .\tests\TaskBoard.Worker.Tests\TaskBoard.Worker.Tests.csproj
```

## Branches and Pull Requests

- Use short, descriptive branch names. Prefix when useful, such as `fix/...`, `feat/...`, or `docs/...`.
- Keep PRs small and focused.
- Link an issue when one exists.
- Include tests for new behavior.
- Update docs when behavior, setup, or public workflow changes.

## Code Style

There is no enforced standalone style guide. Match the surrounding code. Keep changes narrow and consistent with the existing architecture.

Agent-facing project context lives in `memory-bank/*.md`. Those files are structured project documentation with strict rules: current state only, terse, and declarative. See `CLAUDE.md` for details before editing them.

## Commit Messages

Use imperative mood. Add a scope prefix when it clarifies the change, matching the existing history, for example:

- `Fix polling visibility and field metadata casing`
- `Add dependency relationship updates`
- `docs: clarify sandbox setup`

## License

This project does not require a CLA. By submitting a PR, you agree that your contribution will be licensed under the MIT License.
