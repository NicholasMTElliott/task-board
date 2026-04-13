# Docker Sandbox How-To

The Docker sandbox runs each agent invocation inside an isolated container (`aiboard-agent-sandbox` image) instead of invoking the host's Claude CLI directly. This gives you filesystem, network, and resource isolation for agent-generated code.

It is **off by default**. Follow this guide to enable and verify it.

---

## 1. Prerequisites

| Requirement | Check |
|---|---|
| Docker daemon running | `docker info` |
| Claude CLI credentials on host | `~/.claude` exists (auto-mounted into the container) |
| Built `.NET` worker | `dotnet build` succeeds |

---

## 2. Build the sandbox image

One-time (and whenever `docker/agent-sandbox/Dockerfile` changes):

```powershell
.\scripts\build-sandbox.ps1
```

Or via compose:

```powershell
docker compose --profile build up agent-sandbox
```

Verify:

```powershell
docker images aiboard-agent-sandbox
```

You should see `aiboard-agent-sandbox:latest`.

---

## 3. Enable the Docker executor

Set `AGENT_EXECUTOR=docker` in your environment (or `.env.local`):

```powershell
$env:AGENT_EXECUTOR = "docker"
```

At startup the worker probes the Docker daemon (`PrerequisiteValidator.IsDockerAvailableAsync`). If Docker is reachable, the `docker` provider is registered automatically. If not, the worker falls back to `claude-cli` and logs a warning — check the startup log.

### Optional tuning (`appsettings.json`, `Docker` section)

| Key | Default | Purpose |
|---|---|---|
| `ImageName` | `aiboard-agent-sandbox:latest` | Image to run |
| `ReuseContainer` | `true` | Session reuse across steps (one container per run). Set `false` for per-step `docker run`. |
| `NetworkMode` | `host` | `none` for full isolation |
| `MemoryLimit` | *(unset)* | e.g. `4g` |
| `CpuLimit` | *(unset)* | e.g. `2.0` |
| `CredentialPath` | *(auto `~/.claude`)* | Explicit path to Claude credentials |
| `TimeoutSeconds` | `900` | Kill container after N seconds |
| `MaxBudgetUsd` | `10.00` | Per-invocation CLI budget |
| `AdditionalMounts` | `{}` | Extra `-v host:container[:ro]` mounts |

---

## 4. Run an agent on a card

```powershell
$env:AGENT_EXECUTOR = "docker"
.\scripts\run_once.ps1 -CardId 3
```

---

## 5. Verify it worked

### 5.1 Startup logs
Look for `docker` in the registered executor list. `PrerequisiteValidator` also warns on orphaned containers from prior crashes — clean them up with `docker rm -f $(docker ps -aq --filter name=aiboard-)`.

### 5.2 While the run is live
In another terminal:

```powershell
docker ps --filter name=aiboard-
```

Expected:
- **Session mode** (`ReuseContainer=true`): one container named `aiboard-{cardId}` spanning the full run.
- **Per-step mode** (`ReuseContainer=false`): short-lived containers `aiboard-run-{cardId}-{suffix}` appearing/disappearing per step.

Tail its output:

```powershell
docker logs -f aiboard-{cardId}
```

### 5.3 After the run
The container should be gone (`--rm` / session `docker stop` + `docker rm`):

```powershell
docker ps -a --filter name=aiboard-    # expect empty
```

### 5.4 Database metrics (authoritative)
Session-mode runs populate two columns that are `NULL` for host `claude-cli` runs:

```powershell
dotnet run --project lambda/src/TaskBoard.Worker -- --mode metrics --card-id 3
```

- `agent_run.session_startup_ms` — time to create + start the container
- `step_result.session_exec_ms` — `docker exec` duration per step

If both are `NULL` after a run with `AGENT_EXECUTOR=docker`, the session did not activate. Most common causes:

| Symptom | Cause | Fix |
|---|---|---|
| All steps ran, metrics NULL | Image missing → `TryCreateSessionAsync` returned null, transparent fallback to host CLI | `.\scripts\build-sandbox.ps1` |
| Worker logs "docker daemon not available" | Docker not running | Start Docker Desktop / `systemctl start docker` |
| Container exits 125/126/127 | Docker invocation error (bad mount, bad image) | Check `docker logs`, verify `CredentialPath` |
| Container exits 137 | OOM kill | Raise `MemoryLimit` |

### 5.5 Sanity toggle
Confirm both code paths work by flipping `Docker:ReuseContainer` between `true` and `false` and re-running. Session mode fills the `session_*` columns; per-step mode still runs inside Docker but leaves them `NULL`.

---

## 6. Disable

Either unset `AGENT_EXECUTOR` (defaults back to stub / whatever `appsettings.json` selects) or set it explicitly to `claude-cli`. No image cleanup needed.
