# OpenCode Sandbox How-To

The OpenCode sandbox runs agent invocations inside an isolated Docker container (`aiboard-opencode-sandbox` image) against a **local** Anthropic-compatible llama.cpp server — typically Qwen3.6 served by the sibling `local-llm` compose project. This gives you a free, offline alternative to the Claude-based providers for low-stakes roles.

It is **off by default** and entirely independent of the Docker/Claude sandbox. Follow this guide to enable it, verify it, and understand when it's safe to use.

---

## 1. Prerequisites

| Requirement | Check |
|---|---|
| Docker daemon running | `docker info` |
| `llm-net` bridge network exists | `docker network ls \| Select-String llm-net` |
| `local-llm` compose project running | `docker ps \| Select-String llama-server` |
| Built OpenCode sandbox image | `docker images aiboard-opencode-sandbox` |
| Built `.NET` worker | `dotnet build` succeeds |

If `llm-net` is missing, start the sibling project first:

```powershell
cd ..\local-llm
docker compose up -d
docker network ls | Select-String llm-net   # should now appear
```

---

## 2. Build the sandbox image

One-time, and whenever `docker/opencode-sandbox/` changes:

```powershell
.\scripts\build-opencode-sandbox.ps1
```

Or via compose:

```powershell
docker compose --profile build up opencode-sandbox
```

Verify:

```powershell
docker images aiboard-opencode-sandbox
```

You should see `aiboard-opencode-sandbox:latest`.

---

## 3. Enable the OpenCode executor

Set `AGENT_EXECUTOR=docker-opencode` in your environment (or `.env.local`) to make `docker-opencode` the explicitly-requested executor. Even without this env var, the executor is auto-registered whenever Docker is available; this variable just fails startup loud if Docker isn't reachable:

```powershell
$env:AGENT_EXECUTOR = "docker-opencode"
```

At startup the worker logs:

```
DockerOpenCodeAgentOptions: ImageName=aiboard-opencode-sandbox:latest, NetworkMode=llm-net, ProviderBaseUrl=http://llama-server:8080, ModelName=qwen3.6-35b-a3b
OpenCode executor registered. Ensure the 'llm-net' Docker network exists (start the local-llm compose project) before routing roles to 'docker-opencode'.
```

Having the executor registered is not the same as using it — nothing routes to `docker-opencode` until you point a role at it in your workflow config.

### Optional tuning (`appsettings.json`, `DockerAgents:OpenCode` section)

```json
{
  "DockerAgents": {
    "OpenCode": {
      "ImageName": "aiboard-opencode-sandbox:latest",
      "NetworkMode": "llm-net",
      "ProviderBaseUrl": "http://llama-server:8080",
      "AuthToken": "local",
      "ModelName": "qwen3.6-35b-a3b",
      "TimeoutSeconds": 600,
      "MaxRetriesOnMalformedOutput": 2
    }
  }
}
```

| Key | Default | Purpose |
|---|---|---|
| `ImageName` | `aiboard-opencode-sandbox:latest` | Image to run. |
| `NetworkMode` | `llm-net` | Must match the bridge network owned by `local-llm`. Change only if you renamed that network. |
| `ProviderBaseUrl` | `http://llama-server:8080` | Anthropic-compatible API endpoint inside the network. |
| `AuthToken` | `local` | Dummy token — llama.cpp validates nothing. Any non-empty string works. |
| `ModelName` | `qwen3.6-35b-a3b` | Model alias requested from llama-server. Must match the alias in `local-llm/docker-compose.yml`. |
| `TimeoutSeconds` | `600` | Cold prefix cache on first request can take 1–2 min. Keep the timeout generous. |
| `MaxRetriesOnMalformedOutput` | `2` | Retry budget when the model response doesn't parse as Agent Contract JSON. After the final attempt, the executor returns `outcome: ERROR` with raw output in detail rather than throwing. |
| `ContainerNamePrefix` | `aiboard-oc` | Prefix for generated container names (shape: `aiboard-oc-{tenantHash}-{cardId}-{rand}`). Keep the `aiboard-` prefix so orphaned-container detection still matches. |
| `RateLimitPatterns` | `[]` | Additional stderr substrings that should be treated as rate-limit signals, merged with the built-in Anthropic patterns. |

---

## 4. Route a role to OpenCode

The executor is registered under provider key `docker-opencode`. Opt in by changing the role's `provider` in `workflow.github.json`:

```json
"gate_checker": {
  "model": "qwen3.6-35b-a3b",
  "provider": "docker-opencode",
  "systemPromptFile": "prompts/gate_checker.md",
  "sections": []
}
```

Any unrecognised `provider` value fails config validation at startup.

---

## 5. Smoke-test the round-trip

```powershell
.\scripts\smoke-opencode.ps1
```

The script:

1. Confirms Docker, the image, and the `llm-net` network are all present.
2. Runs a minimal one-shot against the sandbox with a trivial prompt.
3. Parses the response via the same JSON-extraction strategies the executor uses (fenced block → whole document → trailing balanced braces).
4. Asserts the response contains a valid `outcome` (`COMPLETE`, `NEEDS_INFO`, or `ERROR`).

On failure, the script prints the raw stdout/stderr and a suggested fix.

---

## 6. Role suitability (**read this before routing roles to Qwen**)

Qwen3.6-35B-A3B, served with `--reasoning off` as configured in `local-llm`, is well-suited to tool-heavy, structured-loop roles:

- `gate_checker` — lightweight pass/fail validations
- `estimator` — size a ticket against a calibration point
- `code_reviewer` — inspect a diff for obvious issues
- `implementer` — mechanical edits following a clear design

It is **not currently recommended** for reasoning-heavy roles — `senior_engineer` (design), `qa` (test-plan generation), `specialist_reviewer`, `senior_specialist_reviewer` — unless you restart `llama-server` with `--reasoning on`, which is a separate operational mode and has much slower first-request latency.

This is **guidance, not enforcement**. The executor will run any role you point at it; the outcome is on you. Part 2 of this initiative (multi-agent candidate evaluation) is intended to replace this guidance with data.

---

## 7. Useful diagnostics

- Orphan cleanup: startup warns on leftover `aiboard-oc-*` containers with a `docker rm -f` suggestion.
- Network errors fire a stderr hint (category `Network`) pointing to likely fixes (`llm-net` absent, `could not resolve host`, etc.).
- Model errors fire a stderr hint (category `Model`) when the requested alias isn't loaded on llama-server.
- The executor logs the resolved `ProviderBaseUrl` and `ModelName` at Info on every run — verify the expected values appear in logs.

---

## 8. Known limitations

- **Single-slot server.** `local-llm`'s `llama-server` runs with `--parallel 1`. Two concurrent agent invocations against the same server will destroy each other's prefix caches and force full prompt reprocessing (~1–2 min). `--mode polling` runs one card at a time, which matches. For parallel loads, add a second `llama-server` on a different port and route explicitly.
- **No server-side schema enforcement.** OpenCode has no `--json-schema` flag and this executor does not pass one through. Structured output is prompt-engineered and validated client-side by `OpenCodeOutputParser`. The retry loop papers over occasional misses but is not as strict as Claude's schema-enforced output.
- **128K context ceiling.** Local llama.cpp is configured for 128K. Oversized prompts fail at the server boundary (stderr hint `context length`).
- **No credential staging.** Unlike the Claude sandbox, no host directory is mounted into the container — the connection detail is just env vars passed through to the entrypoint.
