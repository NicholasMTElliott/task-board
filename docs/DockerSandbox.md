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

Set `AGENT_EXECUTOR=docker-claude-cli` in your environment (or `.env.local`):

```powershell
$env:AGENT_EXECUTOR = "docker-claude-cli"
```

At startup the worker probes the Docker daemon (`PrerequisiteValidator.IsDockerAvailableAsync`). If Docker is reachable, the `docker-claude-cli` provider is registered and `claude-cli` roles are transparently routed through it. If the daemon is unreachable, startup fails fast with a diagnostic message.

### Optional tuning (`appsettings.json`, `DockerAgents:Claude` section)

The configuration section was renamed from `Docker` to `DockerAgents:Claude` to make room for future Docker-wrapped executors. The legacy `Docker` section is still honoured, but startup logs a deprecation warning whenever it is populated — migrate to the new section to silence it.

Example (in `appsettings.user.json`):

```json
{
  "AgentExecutor": "docker-claude-cli",
  "DockerAgents": {
    "Claude": {
      "ImageName": "aiboard-agent-sandbox:latest",
      "ReuseContainer": true,
      "NetworkMode": "host",
      "TimeoutSeconds": 900,
      "MaxBudgetUsd": 10.00
    }
  }
}
```

| Key | Default | Purpose |
|---|---|---|
| `ImageName` | `aiboard-agent-sandbox:latest` | Image to run |
| `ReuseContainer` | `true` | Session reuse across steps (one container per run). Set `false` for per-step `docker run`. |
| `NetworkMode` | `host` | Forwarded to `docker run --network`. `host` lets the agent reach host-published ports from your local `docker-compose` support stack (Postgres `localhost:5432`, Grafana `localhost:3000`, etc.). Use a compose network name (e.g. `task-board_default` — see `docker network ls`) to reach services by service name instead. `none` for full isolation. Empty value omits the flag (Docker default bridge). **Note:** host networking on Docker Desktop for Windows/Mac requires 4.34+ with *Enable host networking* turned on in Settings → Resources → Network; on older versions it silently falls back to bridge. |
| `MemoryLimit` | *(unset)* | Forwarded to `docker run --memory` when set. e.g. `4g` |
| `CpuLimit` | *(unset)* | Forwarded to `docker run --cpus` when set. e.g. `2.0` |
| `ContainerUser` | *(unset)* | Forwarded to `docker run --user` when set. e.g. `1000:1000` |
| `CredentialPath` | *(auto `~/.claude`)* | Source for the per-run staged RW copy mounted into the container (so the Claude CLI can create `session-env/` at runtime). Host `~/.claude/` is never written to by the agent. Large subdirs (`projects`, `shell-snapshots`, `todos`, `history`) are skipped during the copy. |
| `CredentialMountPoint` | `/home/agent/.claude` | Container-side target for the staged credentials. Matches the `agent` user's home directory in the default sandbox image. |
| `PromptMountPoint` | `/mnt/aiboard/prompts` | Read-only mount for system-prompt files. |
| `TimeoutSeconds` | `900` | Kill container after N seconds |
| `MaxBudgetUsd` | `10.00` | Per-invocation Claude CLI budget |
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

If both are `NULL` after a run with `AGENT_EXECUTOR=docker-claude-cli`, the session did not activate. Most common causes:

| Symptom | Cause | Fix |
|---|---|---|
| All steps ran, metrics NULL | Image missing → `TryCreateSessionAsync` returned null, transparent fallback to host CLI | `.\scripts\build-sandbox.ps1` |
| Worker logs "docker daemon not available" | Docker not running | Start Docker Desktop / `systemctl start docker` |
| Container exits 125/126/127 | Docker invocation error (bad mount, bad image) | Check `docker logs`, verify `CredentialPath` |
| Container exits 137 | OOM kill | Raise `MemoryLimit` |

### 5.5 Sanity toggle
Confirm both code paths work by flipping `DockerAgents:Claude:ReuseContainer` (legacy: `Docker:ReuseContainer`) between `true` and `false` and re-running. Session mode fills the `session_*` columns; per-step mode still runs inside Docker but leaves them `NULL`.

---

## 6. Disable

Either unset `AGENT_EXECUTOR` (defaults back to stub / whatever `appsettings.json` selects) or set it explicitly to `claude-cli`. No image cleanup needed.
