# Claude-CLI-on-Qwen Sandbox How-To

This sandbox runs the **Claude CLI** inside Docker but redirects it to the **local llama.cpp proxy** (typically Qwen3.6 served by the sibling [`local-llm`](../../local-llm/) project) instead of `api.anthropic.com`. It's the third Qwen-target executor in the system, sitting alongside the OpenCode sandbox so the two can be A/B'd through the candidate-evaluation feature.

It is **off by default** and entirely independent of the other two Docker executors. Follow this guide to enable it, verify it, and understand when to choose it over OpenCode.

---

## Why this exists alongside `docker-opencode`

We have two executors that target the same local Qwen server:

| | `docker-opencode` | `docker-claude-qwen` |
|---|---|---|
| CLI | OpenCode (`opencode-ai`) | Claude CLI (`claude`) |
| Image | `aiboard-opencode-sandbox:latest` | `aiboard-agent-sandbox:latest` (reuses the existing Claude sandbox) |
| Schema enforcement | Prompt-engineered + client-side parser + retry loop | **Server-side** via the Claude CLI's `--json-schema` → tool-call mechanism, forwarded by the proxy and constrained by llama.cpp's `qwen3_coder` jinja template |
| Wire format | OpenAI-compatible (`/v1/chat/completions`) | Anthropic Messages (`/v1/messages`) |
| Setup overhead | Synthetic `opencode.json` from env vars | Synthetic `~/.claude/settings.json` + redirect env vars |
| Failure modes | Output won't parse; retry loop kicks in | Schema rejected at the wire; CLI errors with a clear message |

Both are workable. `docker-claude-qwen` is the better choice when you specifically need wire-enforced schema reliability (the candidate-evaluation evaluator is the headline case). `docker-opencode` is the lighter-weight choice for free-form coding loops.

The candidate-evaluation feature ([docs/CandidateEvaluation.md](CandidateEvaluation.md)) is the data-driven path to deciding which one wins per role over time.

---

## 1. Prerequisites

| Requirement | Check |
|---|---|
| Docker daemon running | `docker info` |
| `llm-net` bridge network exists | `docker network ls \| Select-String llm-net` |
| `local-llm` compose project running | `docker ps \| Select-String llama-server` |
| Claude sandbox image built | `docker images aiboard-agent-sandbox` |
| `.NET` worker built | `dotnet build` succeeds |

If `llm-net` is missing, start the sibling project first:

```powershell
cd ..\local-llm
docker compose up -d
```

If the Claude sandbox isn't built:

```powershell
.\scripts\build-sandbox.ps1
```

This is the same image used by `docker-claude-cli` (real Anthropic) — no separate image is needed for the Qwen-target variant. The Qwen redirect happens entirely at runtime via env vars.

---

## 2. Enable the executor

Set `AGENT_EXECUTOR=docker-claude-qwen` to make it the explicitly-requested executor (fails fast if Docker is unavailable). Even without this, the executor is auto-registered whenever Docker is detected:

```powershell
$env:AGENT_EXECUTOR = "docker-claude-qwen"
```

At startup the worker logs:

```
DockerClaudeQwenAgentOptions: ImageName=aiboard-agent-sandbox:latest, NetworkMode=llm-net, ProviderBaseUrl=http://llama-server:8080, ModelName=qwen3.6-35b-a3b, AttributionHeader=off, NonessentialTraffic=off
Claude→Qwen executor registered. Ensure the 'llm-net' Docker network exists (start the local-llm compose project) before routing roles to 'docker-claude-qwen'.
```

The `AttributionHeader=off` and `NonessentialTraffic=off` lines confirm the cache-friendliness toggles are active — both prevent Claude CLI from busting llama.cpp's prefix cache or hitting `api.anthropic.com` for telemetry.

### Optional tuning (`appsettings.json`, `DockerAgents:ClaudeQwen` section)

```json
{
  "DockerAgents": {
    "ClaudeQwen": {
      "ImageName": "aiboard-agent-sandbox:latest",
      "NetworkMode": "llm-net",
      "MountHostDockerSocket": false,
      "ProviderBaseUrl": "http://llama-server:8080",
      "AuthToken": "local",
      "ModelName": "qwen3.6-35b-a3b",
      "MaxBudgetUsd": 50.00,
      "TimeoutSeconds": 600,
      "DisableAttributionHeader": true,
      "DisableNonessentialTraffic": true,
      "PerformanceVolumes": []
    }
  }
}
```

| Key | Default | Purpose |
|---|---|---|
| `ImageName` | `aiboard-agent-sandbox:latest` | Reuses the existing Claude sandbox. |
| `NetworkMode` | `llm-net` | Bridge network owned by `local-llm`. |
| `MountHostDockerSocket` | `false` | When true, bind-mounts the host Docker daemon socket into the sandbox. Use with a project overlay that installs Docker CLI / Compose when this executor must run Docker-backed verification commands. Grants host-Docker control. |
| `HostDockerSocketPath` | `/var/run/docker.sock` | Host socket path used when `MountHostDockerSocket=true`. |
| `ContainerDockerSocketPath` | `/var/run/docker.sock` | Container socket path used when `MountHostDockerSocket=true`. |
| `ProviderBaseUrl` | `http://llama-server:8080` | **No `/v1` suffix** — Claude CLI's Anthropic Messages adapter appends `/v1/messages` itself. (Compare with the OpenCode default which DOES include `/v1` because OpenAI-compat appends `/chat/completions`.) |
| `AuthToken` | `local` | llama.cpp accepts any non-empty token; this is a local dummy. |
| `ModelName` | `qwen3.6-35b-a3b` | **Default** model when the workflow role doesn't pin one. Per-role `model` overrides this. |
| `MaxBudgetUsd` | `50.00` | Per-call cost ceiling forwarded as `--max-budget-usd`. Generous since Qwen is free; the flag is still set so we exercise the same code path as real-Anthropic runs. |
| `TimeoutSeconds` | `600` | Cold prefix cache on first request can take 1–2 min. |
| `DisableAttributionHeader` | `true` | Sets `CLAUDE_CODE_ATTRIBUTION_HEADER=0` so the CLI doesn't change request headers per call (which busts the prefix cache). Recommended on per the model card. |
| `DisableNonessentialTraffic` | `true` | Sets `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` so the CLI doesn't ping `api.anthropic.com` for telemetry / feature flags. |
| `ContainerNamePrefix` | `aiboard-cq` | Prefix on container names: `aiboard-cq-{tenantHash}-{cardId}-{rand}`. |
| `RateLimitPatterns` | `[]` | Operator-extensible stderr substrings, merged with the built-in Anthropic patterns. |
| `PerformanceVolumes` | `[]` | Workspace-relative dependency/cache directories to shadow with Docker named volumes. Use only for reproducible folders such as `node_modules`, `.pnpm-store`, `.gradle`, `target`, or `.godot/imported`. |
| `PerformanceVolumeOwner` | `agent:agent` | Owner applied the first time a performance volume is initialized. Empty skips ownership initialization. |

Performance volumes are opt-in and deterministic per worktree/path. They help when a project's tests traverse large dependency trees through Docker Desktop's Windows bind-mount layer; source files and commit-required build outputs should stay on the normal worktree bind mount.

### How the credential staging works

Unlike `docker-claude-cli` (real Anthropic), this executor does **not** mount the operator's `~/.claude` from the host — that would risk leaking real Anthropic credentials into a Qwen-target run. Instead the mount builder writes a synthetic `~/.claude` directory to a temp dir on each call:

- `settings.json` — `hasCompletedOnboarding: true` (bypasses CLI onboarding) plus the env-var overrides as a backup signalling layer.
- `credentials.json` — dummy `{ "anthropicApiKey": "local" }` so the CLI's startup credential probe finds a file.

The temp dir is mounted RW at `/home/agent/.claude` and cleaned up when the mount context disposes. Connection routing is *also* forwarded as docker `-e` env vars (`ANTHROPIC_BASE_URL`, `ANTHROPIC_AUTH_TOKEN`, `ANTHROPIC_API_KEY`, `ANTHROPIC_MODEL`) — belt and braces, so the CLI honours the override regardless of which path it reads first.

---

## 3. Route a role to it

```json
"gate_checker": {
  "model": "qwen3.6-35b-a3b",
  "provider": "docker-claude-qwen",
  "systemPromptFile": "prompts/gate_checker.md",
  "sections": []
},
"senior_engineer": {
  "model": "qwen3.6-35b-a3b-think",
  "provider": "docker-claude-qwen",
  "systemPromptFile": "prompts/senior_engineer.md",
  "sections": ["Technical Design", "Decisions", "Implementation"]
}
```

The `model` field selects between the two Qwen variants; the proxy's `-think` alias rewrites the request to enable reasoning without changing the underlying weights or endpoint. Same per-call mechanism as `docker-opencode`.

---

## 4. A/B test against `docker-opencode`

The reason both executors exist is so the candidate-evaluation feature can pit them against each other on the same task. Configure a step with two candidates:

```json
{
  "name": "implement",
  "role": "implementer",
  "candidates": [
    { "provider": "docker-opencode",   "model": "qwen3.6-35b-a3b" },
    { "provider": "docker-claude-qwen", "model": "qwen3.6-35b-a3b" }
  ],
  "evaluator": {
    "role": "evaluator",
    "taskPromptFile": "prompts/evaluator/code_review_candidates.md",
    "scoring": "WinnerWithScores"
  }
}
```

The evaluator picks a winner per run; per-(role, provider) win rate, average quality score, and average duration accumulate in `v_provider_role_metrics` over time. After enough runs you can decide whether to standardise on one or keep both.

See [docs/CandidateEvaluation.md](CandidateEvaluation.md) for the full mechanics.

**A note on serialisation**: both candidates target the same `llama-server`, which runs with `--parallel 1`. The candidate executor runs candidates sequentially (not in parallel) per step, so this is fine — but if you ever change that, two concurrent candidate calls would destroy each other's prefix caches and force full prompt reprocessing on every turn.

---

## 5. Useful diagnostics

- Orphan cleanup: startup warns on leftover `aiboard-cq-*` containers from prior crashed runs.
- Stderr signatures (via `CliFailureHintDetector.ClaudeQwenSignatures`) catch Network / Model / Auth / Config / Schema failures and surface a category + actionable hint at the top of the error log.
- The executor logs effective `ModelName` and `ProviderBaseUrl` at Info on every run — verify the expected values appear in logs.
- If you see `hasCompletedOnboarding`-related errors, the synthetic settings.json failed to mount; check the mount-context disposal logs for hints about the temp dir.

---

## 6. Known limitations

- **Single-slot server.** `local-llm`'s `llama-server` runs with `--parallel 1`. Two concurrent agent invocations against the same server will destroy each other's prefix caches. The candidate executor serialises calls per step; orchestrator polling runs one card at a time.
- **128K context ceiling.** llama.cpp is configured for 128K. Oversized prompts fail at the server boundary (stderr hint category `Model`).
- **Claude CLI version drift.** The CLI evolves; flag changes between versions are caught by stderr-signature category `VersionDrift`. If you see this, the CLI image needs rebuilding against the same Claude CLI version your real-Anthropic `docker-claude-cli` runs on, so the two stay aligned.
- **No session reuse (yet).** The real-Anthropic `docker-claude-cli` supports `IAgentExecutorSession` for multi-step container reuse; this Qwen-target variant does not. Each step spawns a new container. Reasonable for the candidate-evaluation use case (each candidate uses a different worktree mount anyway), but if you route this executor to non-candidate workflow roles you'll pay a fresh container startup per step. Add it if the latency matters.
- **Telemetry-vs-cache trade-off.** `CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1` suppresses Claude CLI's feature-flag checks. If a future CLI feature requires those checks to enable, you'll need to set the flag back to `0` and accept the prefix-cache cost.

---

## 7. Project-specific tooling

This executor reuses the Claude sandbox image (`aiboard-agent-sandbox:latest`) — overlays you build for `docker-claude-cli` apply here automatically. See [ProjectOverlays.md](ProjectOverlays.md) for the `FROM aiboard-agent-sandbox:latest` pattern; remember to set both `DockerAgents:Claude:ImageName` and `DockerAgents:ClaudeQwen:ImageName` to the same overlay tag.
