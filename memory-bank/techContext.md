# Tech Context

## Technology Stack

### Core
| Layer | Technology | Notes |
|-------|------------|-------|
| Language | C# / .NET 10 | Orchestrator and worker |
| Agent executor (sandboxed Claude) | Claude CLI inside Docker | `DockerClaudeAgentExecutor`; provider key `docker-claude-cli`; image `aiboard-agent-sandbox:latest`; default Claude path |
| Agent executor (sandboxed Codex) | OpenAI Codex CLI inside Docker | `DockerCodexAgentExecutor`; provider key `docker-codex`; image `aiboard-codex-sandbox:latest`; container is the security boundary so `Yolo=true` by default. Auth uses per-run staged `~/.codex` credential-file RO mounts plus `CODEX_INSTALLATION_ID`. See [docs/CodexSandbox.md](docs/CodexSandbox.md). |
| Agent executor (local LLM, OpenCode) | OpenCode CLI inside Docker | `DockerOpenCodeAgentExecutor`; provider key `docker-opencode`; image `aiboard-opencode-sandbox:latest`; targets local llama.cpp on `llm-net`; supports `qwen3.6-35b-a3b` and `qwen3.6-35b-a3b-think`; schema path is prompt-engineered parser + retry + optional no-think structurer |
| Agent executor (local LLM, Claude CLI) | Claude CLI inside Docker | `DockerClaudeQwenAgentExecutor`; provider key `docker-claude-qwen`; reuses `aiboard-agent-sandbox:latest`; targets llama.cpp through Anthropic Messages; schema enforced through `--json-schema` tool-call path |
| Agent executor (host Claude, **--unsafe required**) | Claude CLI subprocess | `ClaudeAgentExecutor`; provider key `claude-cli`; `--output-format stream-json` + `--json-schema`; bypasses Docker filesystem isolation |
| Agent executor (host Codex, **--unsafe required**) | OpenAI Codex CLI subprocess | `CodexAgentExecutor`; provider key `codex`; bypasses Docker filesystem isolation |
| Board provider (primary) | GitHub Projects v2 | `GitHubProjectsClient` via `gh` CLI (GraphQL + REST) |
| Board provider (legacy) | Trello | `TrelloClient` via REST API |
| Git isolation | Git worktrees | `GitWorkspaceManager` |
| Agent sandbox image | `docker/agent-sandbox/Dockerfile` | node:22-slim + Claude CLI + git + ripgrep + curl + ca-certificates; non-root `agent`; base image / UID/GID configurable via build args |
| Image download | `ImageDownloader` + named HttpClient | Bearer auth via `gh auth token` for GitHub user-attachment URLs |
| Workflow config | `workflow.github.json` | File-based, selected via `WORKFLOW_CONFIG_PATH` |
| Example workflow configs | `workflow.github.example.json`, `workflow.simple.example.json`, `workflow.story-decomposition.example.json` | Ship at install root via csproj `<Content Include>` |

### Edge / Ingestion (Legacy Queue Path)
| Layer | Technology | Notes |
|-------|------------|-------|
| Webhook receiver | Cloudflare Worker (TypeScript) | Cost-first prototype ingress |
| Event queue | PGMQ on Neon Postgres | SQL-only install |
| Compute | AWS Lambda | C#/.NET runtime |

### External APIs
| API | Purpose |
|-----|---------|
| GitHub Projects v2 GraphQL | Read project items, move status, resolve field/option IDs |
| GitHub Issues REST dependency API | Read/write native issue dependencies (`blocked_by`, `blocking`) |
| GitHub Issues REST (via `gh`) | Read/update issue body, post/edit comments |
| Trello REST API | Read card state, write sections, post comments, move cards |
| Claude CLI | Agent inference via subprocess or Docker |
| OpenAI Codex CLI | Agent inference via subprocess or Docker |
| OpenCode CLI | Local-LLM inference via Docker |

### Board Provider Selection
| Provider | Implementation | Env Var |
|----------|---------------|---------|
| GitHub Projects | `GitHubProjectsClient` | `BOARD_PROVIDER=github` |
| Trello | `TrelloClient` | `BOARD_PROVIDER=trello` |
| Stub | `StubTaskBoardClient` | `BOARD_PROVIDER=stub` (default) |

Agent executor selection: `AGENT_EXECUTOR` supports `stub`, `docker-claude-cli`, `docker-codex`, `docker-opencode`, `docker-claude-qwen`, or unset auto-detect. Docker-specific selections fail fast when Docker is unavailable. `docker-claude-cli` also registers the `claude-cli` alias for transparent substitution. Host executors (`claude-cli`, `codex`) register only with `--unsafe` / `Unsafe=true`. Multi-executor resolution uses `AgentExecutorResolver`.

## Development Environment
- IDE: Visual Studio Code
- OS: Windows 11
- Shell: PowerShell 7
- Runtime tools: dotnet, node, docker, aws CLI, terraform, git, gh (GitHub CLI)

### Local Execution
- Single secret source: `/.env.local` (gitignored), template: `/.env.example`
- Helper: `scripts/local-secrets.ps1` loads env vars and generates `worker/.dev.vars`
- Run: `.\scripts\run_once.ps1 -CardId N` or `scripts/local-secrets.ps1 -Action run-dotnet -Mode one`

### GitHub Projects Config
Env vars: `GitHubProjects__Owner`, `GitHubProjects__Repo`, `GitHubProjects__ProjectNumber`.
Requires: `gh` CLI authenticated with `project` + `repo` scopes.

### Migration Tooling
- Flyway via Docker (`redgate/flyway`) for SQL-first schema migrations
- Scripts in `db/migrations`, applied via `scripts/migrate.ps1`
- Migration chain: V1 processed_events → V2 pgmq_core → V3 events_queue → V4 card_state → V5 run_log → V6 run_log step_name → V7 pgmq_pings_queue → V8 card_state_claimed_at → V9 agent_run → V10 step_result → V11 drop_run_log → V12 metrics estimate/indexes/views → V13 session timing → V14 failure_reason → V15 multi-tenant table rebuild → V16 tenant-aware metrics views → V17-V22 candidate/slot/usage/reliability/resource metrics → V23 `card_dependency_wait` → V24 rerun-cache columns → V25 `v_cache_hit_rate`

## Decided Architecture Items
- ✅ Board abstraction: `ITaskBoardClient` with GitHub Projects, Trello, and Stub implementations.
- ✅ GitHub Projects integration uses `gh` CLI + GraphQL/REST; body writes use UTF-8 no-BOM temp files (`--body-file` or `gh api --input <temp-json>`), never stdin body piping on Windows. `ProcessRunner.ConfigureUtf8Io` sets UTF-8 stdio plus `PYTHONIOENCODING=utf-8` and `DOTNET_SYSTEM_CONSOLE_UTF8IO=1`.
- ✅ Agent contract: structured outcomes `COMPLETE | NEEDS_INFO | ERROR`; Claude-family executors enforce with `--json-schema`; Codex/OpenCode parsers wrap compatible structured output. See [memory-bank/systemPatterns.md](systemPatterns.md) → Agent Contract.
- ✅ Multi-step states, gate checks, optional specialist reviewers, estimation, story-to-task decomposition, priority propagation, estimate rollup, event-driven parent completion, cross-reference resolution, dependency gating, image download, metrics, direct mode, polling mode, and legacy queue mode are current architecture. See [memory-bank/systemPatterns.md](systemPatterns.md).
- ✅ Failure classification persists `FailureReason` values `RATE_LIMIT`, `AGENT_ERROR`, `INFRASTRUCTURE`, `TIMEOUT` to `agent_run.failure_reason`. `AgentRunner.ClassifyFailure(Exception)` maps `TimeoutException` → `TIMEOUT`, `CliInfrastructureException` → `INFRASTRUCTURE`, everything else → `AGENT_ERROR`; `RateLimitException` has a dedicated restore-and-backoff path.
- ✅ CLI rate-limit detection covers Claude stderr patterns, Claude stdout `rate_limit_event` NDJSON, Codex stderr patterns plus operator `CodexCliLlmOptions.RateLimitPatterns`, and GitHub API 429/abuse/secondary-rate responses.
- ✅ `GetCardAsync` includes project field metadata such as priority and estimate via `gh project item-list`.
- ✅ Docker sandbox images: Claude (`docker/agent-sandbox/Dockerfile`), Codex (`docker/codex-sandbox/Dockerfile`), OpenCode (`docker/opencode-sandbox/`). Build via provider-specific scripts or Docker Compose profiles. Project-specific tooling uses overlay images documented in [docs/ProjectOverlays.md](../docs/ProjectOverlays.md).
- ✅ Docker runtime config is shared through `DockerAgentOptionsBase` / `DockerMountBuilderBase`: image, user, memory/CPU, network mode, reuse, host Docker socket, group-add, additional mounts, performance volumes. `ContainerUser=root` is startup-invalid. `PerformanceVolumes` are workspace-relative Docker named-volume overlays for cache/dependency dirs (`node_modules`, `.pnpm-store`, `.gradle`, `target`, `.godot/imported`), not source or commit-required outputs.
- ✅ Docker workspace mounting uses RW worktree at `/workspace`, RO base `.git` at `/repo/.git`, RO `.git` file override pointing to container-internal gitdir, `GIT_OPTIONAL_LOCKS=0`, and Windows path normalization. Agents do no git writes.
- ✅ Orchestrator owns git writes through `AgentRunner.HandleGitBehaviorAsync`; `gitBehavior` supports `commit_and_push`, `commit_only`, and `discard`. System prompts prohibit git write commands; Docker enforces with RO `.git`; read-only git commands are allowed.
- ✅ `.aiboard/git-mode-changes.txt` lets agents declare executable-bit changes as `+x path` / `-x path`; `GitWorkspaceManager.CommitAsync` applies them with `git update-index --chmod` after `git add .` and before `git commit`.
- ✅ Container sessions use `IAgentExecutorSession` / `ISessionableAgentExecutor`; `DockerClaudeAgentOptions.ReuseContainer=true` by default; sessions span steps, gate checks, and optional reviewers. Timeout/cancellation cleanup uses `docker stop -t 30` then `docker rm -f` best-effort.
- ✅ Orphaned container detection only targets agent-run prefixes (`aiboard-run-`, `aiboard-cdx-`, `aiboard-cq-`, `aiboard-oc-`), not operator support containers.
- ✅ Two-phase graceful shutdown applies to polling and queue modes: first Ctrl+C stops new claims and cancels idle delays; second Ctrl+C hard-cancels. `AgentRunner` inter-step shutdown commits/pushes partial work when configured and restores the card to the trigger column.
- ✅ Shared-column workflows use `WorkflowState.Column`, filters, and `WorkflowConfig.ResolveState`; `--mode agent --state <stateId>` overrides filter-based resolution for direct invocation. `workflow.simple.example.json` demonstrates the pattern.
- ✅ Multi-tenant DB partitioning uses `ITenantIdentifier` (`{provider}:{identifier}` plus 8-char `ShortHash`) on `agent_run`, `step_result`, `card_state`, and `processed_events`; all per-tenant stores filter by `tenant_id`. PGMQ queue names remain global because queue mode is legacy.
- ✅ Config-only invocation resolves required values from merged configuration (`appsettings.json` < `.aiboard/appsettings.json` < `--config` < `appsettings.user.json` < env vars < CLI args). Help appears only on explicit help flags.
- ✅ Startup config validation is strict: unknown provider/executor, incomplete board config, provider/section contradictions, both GitHub and Trello sections populated, legacy `Docker` section, and unsupported `CodexCli:MaxBudgetUsd` abort startup. Sensible defaults are allowed for `WorktreeBasePath`, `PollIntervalSeconds`, `ClaudeCli:ExecutablePath`, and `Stub:TenantName`.
- ✅ `--mode validation` checks workflow static rules, prompt files, live board shape (`IBoardShapeProbe`), and Docker image availability (`IDockerImageProbe`). Findings are grouped by Error/Warning/Info; errors exit 1.
- ✅ `--mode init` scaffolds `./.aiboard/` from bundled `templates/from-scratch-{claude,codex,opencode}/`; substitutes owner/repo/project placeholders; `--force` overwrites template files while preserving operator-added files; runs before DI/provider/DB probing.
- ✅ `--install` copies bundled Claude Code skills from `{exeDir}/skills/<name>/` to `{userHome}/.claude/skills/<name>/`; re-install overwrites bundled files and preserves operator-added destination files.
- ✅ `--mode scaffold-board --board-id N [--apply]` computes a dry-run board shape plan from workflow requirements, creates missing GitHub project single-select fields and labels when applied, treats already-exists as success, rejects comma-containing option names, and leaves existing-field option additions / Status columns as warnings.
- ✅ `--mode diagnose --card-id N` is read-only card pickup triage. It reports card metadata, resolved state or filter failures, assignee-isEmpty lock commands, dependency blockers via `DependencyGuard(recordSideEffects:false)`, and returns 0 on successful diagnosis.
- ✅ Codex defensive diagnostics include startup version logging, prompt/schema/policy command logs, >200 KB prompt warning, stderr hint categories, loud no-structured-output errors, unknown-event/malformed-line warnings, and 4000-char exception stderr cap.
- ✅ `ProcessRunnerDelegate` is injected into CLI executors for subprocess-output replay in tests; production defaults to `ProcessRunner.RunProcessAsync`.
- ✅ `WorkflowConfigValidator.Audit(config)` emits non-fatal warnings such as Codex provider usage without explicit `sandbox` / `yolo` / `fullAuto`, cross-provider candidates without pinned models, and final slot candidates with no retries.
- ✅ Multi-agent candidate evaluation uses `WorkflowStep.Slots` (or legacy `Candidates`+`Evaluator` adapted to one slot), parallel candidate worktrees, per-candidate retries, evaluator verdicts (`winner_index`, `scores`), commit/discard promotion modes, canonical cache rows, and metrics views. See [docs/CandidateEvaluation.md](../docs/CandidateEvaluation.md) and [memory-bank/systemPatterns.md](systemPatterns.md) → CandidateExecutor.
- ✅ Role fallback semantics apply to single-agent invocations and evaluator invocations before a valid result exists. In-band `outcome=ERROR` is a valid judgment and does not fallback. Candidates use per-candidate retries and slot chains instead.
- ✅ `AgentSchemas.EvaluatorOutcomeSchema` and `EvaluatorOutcomeSchemaOpenAI` require `winner_index` with type `["integer","null"]`; parser-side defense rejects `outcome=COMPLETE` without usable `winner_index`.
- ✅ Provider-scoped model inheritance: candidate model defaults to the role model only when candidate provider equals role provider. Cross-provider candidates leave model null unless explicitly pinned; `step_result.model` uses `"(provider default)"` when executor default is used.
- ✅ Evaluator prompts treat legitimate `NEEDS_INFO` candidates as eligible winners. Evaluator `outcome=NEEDS_INFO` means the evaluator itself cannot judge yet and has `winner_index=null`.
- ✅ Candidate rate-limit handling converts exhausted per-candidate rate limits to ERROR-with-rate-limit flag. Slot chains continue when fallback slots are available; only all-failed/all-rate-limited chains bubble a `RateLimitException` to the card restore/backoff path.
- ✅ Deterministic re-run cache uses `RerunCacheGate`, `execution_kind`, `input_hash`, `section_output_hash`, `output_summary`, `source_run_id`, and `source_step_result_id`; `fast_path_hit` remains a compatibility column and is not written.
- ✅ `DockerClaudeQwenAgentExecutor` uses synthetic `~/.claude`, `ANTHROPIC_BASE_URL` without `/v1`, `ANTHROPIC_*` env vars, `DisableAttributionHeader=true`, and `DisableNonessentialTraffic=true`; never mounts real Anthropic credentials.
- ✅ `DockerOpenCodeAgentExecutor` uses env-templated `opencode.json`, `ProviderBaseUrl=http://llama-server:8080/v1`, `NetworkMode=llm-net`, `TimeoutSeconds=7200`, `InactivityTimeoutSeconds=1200`, fatal hint short-circuit for Network/Auth/Config/Path, and no-think structurer fallback (`EnableStructurer=true`, `StructurerModelName=qwen3.6-35b-a3b`, `StructurerTimeoutSeconds=180`).
- ✅ `DockerCodexAgentExecutor` uses `NetworkMode=host`, `ContainerNamePrefix=aiboard-cdx`, `--init`, `Yolo=true`, credential allowlist (`auth.json`, `config.toml`, `cap_sid`, `version.json`, `.personality_migration`), runtime-state exclusion, and `CODEX_INSTALLATION_ID`.
- ✅ CLI version policy uses `CliVersionPolicy.KnownGood`, `CliVersionStartupCheck`, parser corpus fixtures under `lambda/tests/TaskBoard.Worker.Tests/Fixtures/Cli/{cli}/{version}/`, and skip-by-default live tests enabled by `AIBOARD_TEST_LIVE_CLI=1`. Codex minimum is 0.125.0; Claude/OpenCode are logged but unenforced.
- ✅ `board_analyst` role uses `claude-haiku-4-5-20251001` and `prompts/board_analyst.md` for `review_related_tickets`; it forbids source-code reads, subprocess execution, tests/builds/linters, and edits outside `.aiboard/`. Codex candidates for this step should use read-only sandbox provider params.
- ✅ `prompts/evaluator/design_candidates.md`, `code_review_candidates.md`, and `gate_check_candidates.md` provide step-type-specific evaluator rubrics.
- ✅ Unknown CLI flags are rejected by `CliDefinitions.ValidateKnownFlags`; common typo hints map `--validate`, `--metrics`, `--polling`, `--agent`, `--queue`, and `--help-me` to valid usage.
- ✅ Release artifacts publish self-contained single-file assets (`aiboard-{rid}.{zip,tar.gz}`) and framework-dependent assets (`aiboard-{rid}-fdd.{zip,tar.gz}`) for `win-x64`, `linux-x64`, `osx-x64`, and `osx-arm64`.
- ✅ Release output must be self-contained for fresh machines. `lambda/src/TaskBoard.Worker/TaskBoard.Worker.csproj` `<Content Include=...>` ships required docs/scripts/Dockerfiles and excludes repo-internal docs. End-user docs must reference paths present in publish output; repo-internal docs must be excluded.
- ✅ Canonical project `.gitignore` pattern for `.aiboard/`:
  ```gitignore
  .aiboard/*
  !.aiboard/workflow.json
  !.aiboard/appsettings.json
  ```
  Renamed workflow files require an additional `!.aiboard/<name>.json` line. `InitRunner.PrintNextSteps` prints this reminder and does not mutate `.gitignore`.
- ✅ Metrics expansion persists usage/cost tokens, structurer fallback flag, rate-limit event counter, evaluator regression flag, and evaluator prompt char count. `MetricsRunner` conditionally prints Provider × Role Metrics (`Cost$ / InTok / OutTok / Struct%`), Evaluator Reliability, and Cache Hit Rate.
- ✅ `AgentIdentity` is `(FamilyName, MachineName)` plus deterministic provider/model given name. `Generate()` assigns one family per process; `ResolveGivenName(provider, model)` hashes `provider:model` into a 200-entry pool; `FormatAgentName(provider, model)` returns `"GivenName FamilyName on MachineName"` or `DisplayName` when provider/model is null. `agent_run.agent_identity` stores `DisplayName`.

## Open Technical Decisions
- [ ] Webhook/event-driven triggers (currently manual CLI or polling)
- [ ] .NET Lambda deployment model (native AOT vs. managed runtime)
- [ ] IaC toolchain (Terraform vs. SAM vs. CDK)
