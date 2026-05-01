# OpenCode Sandbox How-To

The OpenCode sandbox runs agent invocations inside an isolated Docker container (`aiboard-opencode-sandbox` image) against a **local** Anthropic-compatible llama.cpp server — typically Qwen3.6 served by the sibling `local-llm` compose project. This gives you a free, offline alternative to the Claude-based providers for low-stakes roles.

It is **off by default** and entirely independent of the Docker/Claude sandbox. Follow this guide to enable it, verify it, and understand when it's safe to use.

> **There are two Qwen-target executors.** This one (`docker-opencode`) uses the OpenCode CLI and prompt-engineers the JSON schema with a client-side retry loop. The other (`docker-claude-qwen`, see [ClaudeQwenSandbox.md](ClaudeQwenSandbox.md)) uses the Claude CLI and gets server-side schema enforcement via the proxy's tool-call mechanism. They exist side by side specifically so the candidate-evaluation feature can A/B them — choose based on your trust budget for prompt-engineered structure vs. wire-enforced structure, or run both and let the metrics decide.

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
DockerOpenCodeAgentOptions: ImageName=aiboard-opencode-sandbox:latest, NetworkMode=llm-net, ProviderBaseUrl=http://llama-server:8080/v1, ModelName=qwen3.6-35b-a3b
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
      "ProviderBaseUrl": "http://llama-server:8080/v1",
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
| `ProviderBaseUrl` | `http://llama-server:8080/v1` | OpenAI-compatible endpoint exposed by the local llama.cpp proxy. The `/v1` suffix is required — the OpenCode `@ai-sdk/openai-compatible` adapter appends `/chat/completions` to this prefix. |
| `AuthToken` | `local` | Dummy token — llama.cpp validates nothing. Any non-empty string works. |
| `ModelName` | `qwen3.6-35b-a3b` | **Default** model alias when a workflow role doesn't pin one. Both Qwen3.6 variants (`qwen3.6-35b-a3b` and `qwen3.6-35b-a3b-think`) are registered in the sandbox image; per-role `model` overrides this default. |
| `TimeoutSeconds` | `600` | Cold prefix cache on first request can take 1–2 min. Keep the timeout generous. |
| `MaxRetriesOnMalformedOutput` | `2` | Retry budget when the model response doesn't parse as Agent Contract JSON. After the final attempt, the executor returns `outcome: ERROR` with raw output in detail rather than throwing. **Bypassed for fatal stderr hints** — see "Fatal-hint short-circuit" below. |
| `ContainerNamePrefix` | `aiboard-oc` | Prefix for generated container names (shape: `aiboard-oc-{tenantHash}-{cardId}-{rand}`). Keep the `aiboard-` prefix so orphaned-container detection still matches. |
| `RateLimitPatterns` | `[]` | Additional stderr substrings that should be treated as rate-limit signals, merged with the built-in Anthropic patterns. |

### How the dual-model setup works

The sandbox image bakes an `opencode.json` template that registers **both** Qwen3.6 virtual models against the `@ai-sdk/openai-compatible` adapter, with the active model parameterised via the `OPENCODE_MODEL_NAME` env var. On each `docker run`, the sandbox entrypoint templates the JSON:

```json
{
  "provider": {
    "llama-server": {
      "npm": "@ai-sdk/openai-compatible",
      "options": { "baseURL": "${OPENCODE_PROVIDER_BASE_URL}", "apiKey": "${OPENCODE_AUTH_TOKEN}" },
      "models": {
        "qwen3.6-35b-a3b":       { "name": "Qwen3.6 35B-A3B (no thinking)", "limit": { "context": 131072, "output": 8192 } },
        "qwen3.6-35b-a3b-think": { "name": "Qwen3.6 35B-A3B (thinking)",    "limit": { "context": 131072, "output": 8192 } }
      }
    }
  },
  "model": "llama-server/${OPENCODE_MODEL_NAME}",
  "small_model": "llama-server/qwen3.6-35b-a3b-think",
  "compaction": { "auto": true, "prune": true }
}
```

The executor sets `OPENCODE_MODEL_NAME` from the workflow role's `model` field per call. `small_model` is hardcoded to the `-think` alias because OpenCode uses it for compaction summaries — better-grounded summaries preserve specific identifiers (file paths, function names) that the no-think variant tends to drop. The compact happens only at threshold crossings, so the slower call is amortised. See [local-llm/Qwen-3.6.md](../../local-llm/Qwen-3.6.md) for the full rationale.

`limit.context: 131072` matches the server's `--ctx-size`; OpenCode auto-compacts before hitting that ceiling.

---

## 4. Route a role to OpenCode

The executor is registered under provider key `docker-opencode`. Opt in by changing the role's `provider` in `workflow.github.json` and choosing which Qwen variant fits the role:

```json
"gate_checker": {
  "model": "qwen3.6-35b-a3b",
  "provider": "docker-opencode",
  "systemPromptFile": "prompts/gate_checker.md",
  "sections": []
},
"senior_engineer": {
  "model": "qwen3.6-35b-a3b-think",
  "provider": "docker-opencode",
  "systemPromptFile": "prompts/senior_engineer.md",
  "sections": ["Technical Design", "Decisions", "Implementation"]
}
```

The `model` field selects between the two registered variants:

- `qwen3.6-35b-a3b` — fast, no chain-of-thought. Use for tool-call loops, gate checks, estimation, mechanical implementation.
- `qwen3.6-35b-a3b-think` — same weights with reasoning emitted (~7× tokens, ~30–60s per turn). Use for design, code-review write-ups, QA test plans, summaries — anything single-shot where output quality dominates over latency.

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

Two Qwen3.6 variants are exposed by the local-llm proxy as separate model aliases — pick the right one per role rather than choosing a single global mode.

| Role intent | Variant | Why |
|---|---|---|
| `gate_checker` (pass/fail JSON) | `qwen3.6-35b-a3b` | Latency dominates; reasoning would crush throughput. |
| `estimator` (calibrated size) | `qwen3.6-35b-a3b` | Short, structured answer. |
| `code_reviewer` (write the review document) | `qwen3.6-35b-a3b-think` | Synthesis across the whole prompt; quality > latency. |
| `implementer` (tool-loop edits) | `qwen3.6-35b-a3b` | Multi-turn tool loop; reasoning would multiply wall time per step. |
| `senior_engineer` (design) | `qwen3.6-35b-a3b-think` | Single-shot synthesis where grounded reasoning materially improves output. |
| `qa` (test plan) | `qwen3.6-35b-a3b-think` | Same shape as design — single-shot synthesis. |
| `specialist_reviewer` / `senior_specialist_reviewer` | `qwen3.6-35b-a3b-think` | High-stakes reviews benefit from thinking; volume cost is acceptable. |
| `merge_resolver` (file-edit tool loop) | `qwen3.6-35b-a3b` | Tool loop. |

**Rule of thumb:** single-shot synthesis → `-think`, tool-call loops → base.

**Honest caveats:**

- Qwen3.6 is below Claude Opus on architecture reasoning (SWE-Bench 73.4% vs 80.8%). Don't use it for irreversible architectural calls without human review.
- The `-think` variant occasionally hallucinates identifier names (e.g. invents `TowerNode` when the actual class is `Tower`). Verify before code-gen acts on these.
- First request after `docker compose up` or long idle takes 30–120s (cold prefix cache). `TimeoutSeconds: 600` is sized for this.

This is guidance, not enforcement — the executor will run any role you point at it. The multi-agent candidate evaluation feature ([docs/CandidateEvaluation.md](CandidateEvaluation.md)) is the data-driven path to replacing this table with measured win rates per (role, provider).

---

## 7. Useful diagnostics

- Orphan cleanup: startup warns on leftover `aiboard-oc-*` containers with a `docker rm -f` suggestion.
- Network errors fire a stderr hint (category `Network`) pointing to likely fixes (`llm-net` absent, `could not resolve host`, etc.).
- Model errors fire a stderr hint (category `Model`) when the requested alias isn't loaded on llama-server.
- The executor logs the resolved `ProviderBaseUrl` and `ModelName` at Info on every run — verify the expected values appear in logs.

### Fatal-hint short-circuit (v0.0.23+)

When the stderr signature detector fires with one of the **fatal categories** — `Network`, `Auth`, `Config`, `Path` — the retry-on-malformed-output loop is bypassed. The executor immediately throws `CliInfrastructureException` (recorded as `INFRASTRUCTURE` failure-reason in `agent_run.failure_reason`).

Why: re-prompting cannot recover an unreachable upstream, a rejected token, a missing provider key, or a wire-path mismatch. Without this, a single 502 from llama-server during a polling run could burn 3 × the inactivity timer (~60 min on default settings) before surfacing — a real cost observed in the v0.0.22 KvA run that prompted this fix.

`Model` (e.g. "model not found") is intentionally NOT in the fatal list, since a model could be loaded mid-run on a slow-starting llama-server. Retries continue for that case.

If you see a fatal-hint bail in your logs, the operator-actionable fix is in the hint text itself (e.g. "Docker network 'llm-net' does not exist. Start the local-llm compose project first") — not "give the model another try."

---

## 8. Known limitations

- **Single-slot server.** `local-llm`'s `llama-server` runs with `--parallel 1`. Two concurrent agent invocations against the same server will destroy each other's prefix caches and force full prompt reprocessing (~1–2 min). `--mode polling` runs one card at a time, which matches. For parallel loads, add a second `llama-server` on a different port and route explicitly.
- **Schema enforcement is prompt-engineered, not wire-enforced (today).** llama.cpp itself supports `response_format: {"type":"json_schema", ...}` server-side (per the model card), but the OpenCode CLI doesn't expose a flag to thread it through, so this executor relies on a schema instruction block in the prompt + client-side validation by `OpenCodeOutputParser` + bounded retry. This is less strict than Claude's `--json-schema` enforcement; if you see frequent parse failures on a specific role, the long-term fix is to extend OpenCode's CLI surface (or call llama.cpp directly) rather than scale the retry budget.
- **128K context ceiling.** Local llama.cpp is configured for 128K. Oversized prompts fail at the server boundary (stderr hint `context length`).
- **No credential staging.** Unlike the Claude sandbox, no host directory is mounted into the container — the connection detail is just env vars passed through to the entrypoint.
