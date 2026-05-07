# Task-board Sandbox Overlays

Project-specific overlay images that add the **.NET 10 SDK** on top of the upstream aiboard sandbox images. This is the dogfooding case: when aiboard runs against task-board's own codebase, agents need to compile and test C# — the upstream sandboxes intentionally don't ship a .NET SDK because that's project-specific tooling.

See [`docs/ProjectOverlays.md`](../../docs/ProjectOverlays.md) for the general pattern these files implement.

## Layout

```
docker/overlays/
├── Dockerfile.dotnet         # parameterised by BASE_IMAGE
├── .dockerignore             # build-context empty (no COPY in Dockerfile)
├── build-claude-dotnet.ps1   # → aiboard-agent-sandbox:dotnet
├── build-codex-dotnet.ps1    # → aiboard-codex-sandbox:dotnet
├── build-opencode-dotnet.ps1 # → aiboard-opencode-sandbox:dotnet
└── build-all-dotnet.ps1      # convenience wrapper for all three
```

## Operator workflow

```powershell
# 1. Build the upstream sandboxes (or pull updates from a fresh aiboard install)
./scripts/build-sandbox.ps1
./scripts/build-codex-sandbox.ps1
./scripts/build-opencode-sandbox.ps1

# 2. Build the dotnet overlays on top
./docker/overlays/build-all-dotnet.ps1

# 3. Verify (each prints `dotnet --version`)
docker run --rm aiboard-agent-sandbox:dotnet      dotnet --version
docker run --rm aiboard-codex-sandbox:dotnet      dotnet --version
docker run --rm aiboard-opencode-sandbox:dotnet   dotnet --version
```

## Wiring

Task-board's run scripts ([`scripts/run_polling.ps1`](../../scripts/run_polling.ps1) and [`scripts/run_once.ps1`](../../scripts/run_once.ps1)) export `DockerAgents__*__ImageName` env vars pointing at the `:dotnet` tags so polling agents inside containers can `dotnet build` / `dotnet test` against this repo.

If you want to use stock `:latest` images instead (e.g. operating on a non-.NET project from this same install), unset those env vars or override with `:latest` tags.

## When to rebuild

Per [`docs/ProjectOverlays.md`](../../docs/ProjectOverlays.md): rebuild the overlay after every upstream sandbox rebuild (CLI version bumps, base image swaps, security patches). BuildKit caches the install layer so subsequent rebuilds where only upstream changed are fast.

## Reproducible builds

Default `-DotnetChannel "10.0"` pulls the latest 10.0.x patch at build time. For deterministic CI builds, pin to a specific patch:

```powershell
./docker/overlays/build-all-dotnet.ps1 -DotnetChannel "10.0.100" -Tag "dotnet-10.0.100"
```

This produces tags like `aiboard-agent-sandbox:dotnet-10.0.100` that you can then reference in `DockerAgents:*:ImageName`.
