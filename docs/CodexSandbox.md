# Codex Sandbox How-To

The Codex sandbox runs agent invocations inside an isolated Docker container (`aiboard-codex-sandbox` image) using the OpenAI Codex CLI. Filesystem isolation is provided by Docker, so the agent can run with `--yolo` and full autonomy — its writes only land inside the container's view of the worktree, and the host repo / dotfiles / SSH keys / shell history are out of reach.

This is the recommended way to run Codex against your codebase. The host-CLI variant (`codex` provider key) is **off by default** as of this version and requires explicit `--unsafe` opt-in.

---

## 1. Prerequisites

| Requirement | Check |
|---|---|
| Docker daemon running | `docker info` |
| Built Codex sandbox image | `docker images aiboard-codex-sandbox` |
| Codex authenticated on the host | `codex login` (creates `~/.codex/auth.json`) |
| Built `.NET` worker | `dotnet build` succeeds |

The container reads `~/.codex/` from the host via a per-run staged copy. Each top-level credential file (`auth.json`, `config.toml`, `instructions.md`, etc.) is mounted **read-only** under the agent-owned `/home/agent/.codex/` directory inside the container; the directory itself is image-baked and writable by the agent user, so Codex's runtime `mkdir sessions/` succeeds. The host's `~/.codex/` is never mutated — your login session is read, not written. Token refreshes during the run land on the staged RO file (silent no-op) and don't persist; to pick up a refreshed token, run `codex login` on the host again.

> **Why per-file RO and not a dir-level RW mount?** On Docker Desktop Windows + WSL2 the bind-mounted Windows-temp directory's effective permissions inside the container don't allow the non-root `agent` user to `mkdir sessions/` inside it (EPERM via gRPC FUSE / virtiofs). Mounting individual files into the agent-owned image-baked directory keeps the directory writable and avoids the permission-translation issue. Subdirectories that survive the staging copy (only top-level files of `~/.codex/` are typically present after the `sessions`/`log`/`screenshots` exclusions) are intentionally NOT mounted, since a subdir mount would re-introduce the issue.

---

## 2. Build the sandbox image

One-time, and whenever `docker/codex-sandbox/` changes:

```powershell
.\scripts\build-codex-sandbox.ps1
```

Build args (all optional):

- `-BaseImage` (default: `node:22-slim`)
- `-CodexCliVersion` (default: `latest`; pin a specific version like `0.125.0` for reproducibility)
- `-AgentUid` / `-AgentGid` (default: `1000`)
- `-Tag` (default: `latest`)
- `-NoCache`

Verify:

```powershell
docker images aiboard-codex-sandbox
```

---

## 3. Enable the Codex sandbox executor

Provider key: `docker-codex`. The executor is auto-registered when Docker is detected at startup — no environment variable needed for the executor to be available.

To force-fail startup if Docker isn't reachable (useful in CI / production environments where missing Docker is a misconfiguration):

```powershell
$env:AGENT_EXECUTOR = "docker-codex"
```

At startup the worker logs:

```
docker-codex executor registered (image=aiboard-codex-sandbox:latest, network=host)
```

Having the executor registered is not the same as using it — nothing routes to `docker-codex` until you point a role at it in your workflow config.

---

## 4. Wire it into a workflow role

### Per-role assignment (recommended)

Set `provider` on each role you want to route through the sandboxed Codex:

```json
{
  "roles": {
    "implementer": {
      "provider": "docker-codex",
      "model": "gpt-5.4-mini",
      "systemPromptFile": "prompts/senior_engineer.md",
      "sections": ["Implementation"]
    }
  }
}
```

This is the recommended pattern — different roles can use different providers (e.g. `senior_engineer` on `docker-claude-cli`, `implementer` on `docker-codex`, `gate_checker` on `docker-opencode`).

### Whole-workflow default

To route every role through `docker-codex` without per-role provider fields, leave `provider` off the roles and set `AGENT_EXECUTOR=docker-codex` at the process level:

```powershell
$env:AGENT_EXECUTOR = "docker-codex"
.\scripts\run_polling.ps1
```

This is useful when migrating an existing workflow that previously used `codex` (host) — instead of editing every role, just flip the env var. Startup will fail loudly if Docker isn't reachable.

### Per-step `providerParams` overrides

Both patterns above support per-step `providerParams` to override the executor's defaults for a single step:

```json
{
  "name": "implement",
  "role": "implementer",
  "taskPromptFile": "prompts/states/ready_for_implementation.md",
  "providerParams": {
    "yolo": "false",
    "sandbox": "workspace-write"
  }
}
```

---

## 5. Optional tuning (`appsettings.json`, `DockerAgents:Codex` section)

```json
{
  "DockerAgents": {
    "Codex": {
      "ImageName": "aiboard-codex-sandbox:latest",
      "NetworkMode": "host",
      "ContainerNamePrefix": "aiboard-cdx",
      "TimeoutSeconds": 7200,
      "InactivityTimeoutSeconds": 1200,
      "Yolo": true,
      "FullAuto": false,
      "Sandbox": null,
      "CredentialPath": null,
      "CredentialMountPoint": null
    }
  }
}
```

| Key | Default | Notes |
|---|---|---|
| `ImageName` | `aiboard-codex-sandbox:latest` | Override to use a tag-pinned build |
| `NetworkMode` | `host` | Codex needs outbound HTTPS to api.openai.com. Differs from `docker-opencode` / `docker-claude-qwen` which target `llm-net` |
| `ContainerNamePrefix` | `aiboard-cdx` | Per-run name prefix (`{prefix}-{tenant}-{cardId}-{rand}`) |
| `TimeoutSeconds` | `7200` | Hard wall-clock cap |
| `InactivityTimeoutSeconds` | `1200` | "Stuck" detection threshold; null disables |
| `Yolo` | `true` | Container is the sandbox, so default is permissive — see "Why yolo by default" below |
| `FullAuto` | `false` | Only relevant when `Yolo=false` |
| `Sandbox` | `null` | `read-only` / `workspace-write` / `danger-full-access` — only when `Yolo=false` |
| `CredentialPath` | auto-detect `~/.codex` | Override if Codex auth lives elsewhere |
| `CredentialMountPoint` | `/home/agent/.codex` | Container-side parent dir for per-file credential mounts (each top-level file is mounted RO at `{CredentialMountPoint}/{filename}`) |

---

## 6. Why `Yolo = true` by default

The Codex CLI's own `--sandbox` flag restricts filesystem writes to the workspace and forbids escape paths. Inside `aiboard-codex-sandbox` that protection is redundant: the host filesystem isn't reachable from the container, only the worktree mount is writable, and the `.git` directory is mounted read-only so the agent literally cannot push or rewrite history. The container itself is the security boundary.

Defaulting to `--yolo` lets the agent move fast inside its sandbox without approval prompts. If you want belt-and-braces, set `Yolo: false` plus `Sandbox: "workspace-write"` (or `"read-only"` for very low-trust steps) in `providerParams`.

---

## 7. Verify end-to-end

Smoke test against a real card:

```powershell
.\scripts\run_once.ps1 -CardId 3
```

In the logs you should see:

```
Launching Docker/Codex agent for card 3 in C:\..., model=gpt-5.4-mini, image=aiboard-codex-sandbox:latest, container=aiboard-cdx-...
Docker/Codex effective policy: yolo=True (config), fullAuto=False (config), sandbox=(unset) ((none))
NDJSON parsing complete: ... structuredOutputEvents=1, ...
Docker/Codex complete, outcome=COMPLETE, ...
```

---

## 8. Comparison vs. the host `codex` executor

| | `docker-codex` | `codex` (host) |
|---|---|---|
| Provider key | `docker-codex` | `codex` |
| Filesystem isolation | Container | Codex CLI's `--sandbox` only |
| Default `Yolo` | `true` (safe) | `false` |
| Available without `--unsafe` | yes (default) | no — requires `--unsafe` |
| Credentials | Per-run staged copy of `~/.codex` | Direct host process |
| Network | host (or operator-restricted) | host |
| Image build needed | yes (one-time) | no |
| Best for | All workloads against a real codebase | Single-machine ad-hoc dev where you trust the agent |

Migrate workflow roles from `codex` → `docker-codex` to keep working without `--unsafe`.

---

## 9. Troubleshooting

**`No Codex credential path configured or detected`** — run `codex login` on the host. The `~/.codex/auth.json` file must exist before the executor's mount builder can stage a copy.

**`docker: invalid reference format` / image pull errors** — the sandbox image isn't built. Run `.\scripts\build-codex-sandbox.ps1`.

**`OPENAI_API_KEY not set`** — Codex CLI uses ChatGPT-account login by default (`~/.codex/auth.json`). API-key auth is a separate mode; stick with `codex login`.

**Containers piling up under `docker ps -a`** — `docker run --rm` should clean up on normal exit. If you see persistent `aiboard-cdx-*` containers, the host process was killed mid-run; clean up with `docker rm -f $(docker ps -aq --filter name=aiboard-cdx-)`. The startup orphaned-container detector logs a warning when it sees them.

**Agent runs but seems to ignore the workspace** — check that `WorkspacePath` points at a real git worktree (Codex expects `.git` to be reachable). The host log warns when the workspace has no `.git`.

**Codex stderr: `Operation not permitted (os error 1)` / `Codex cannot access session files at /home/agent/.codex/sessions`** — your sandbox image is from before v0.0.22, when credentials were mounted as a single dir-level RW mount. On Docker Desktop Windows + WSL2 the bind-mounted Windows-temp directory's effective permissions inside the container don't allow the non-root `agent` user to `mkdir sessions/` inside it. **Fix**: rebuild the sandbox image (`.\scripts\build-codex-sandbox.ps1`); the Dockerfile now pre-creates `/home/agent/.codex` agent-owned, and the mount builder switched to per-file RO mounts, sidestepping the bind-mount permissions issue. If you see this error AFTER rebuilding, file an issue — it likely means a new credential file shape is reaching the container.
