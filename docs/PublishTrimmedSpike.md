# Publish-Trimmed Spike: Plan

**Status**: Not started. Pick up on a dedicated branch.
**Branch name (suggested)**: `experiment/publish-trimmed`
**Owner**: Whoever drives the spike.

## Context

The v0.0.22 self-contained single-file binary is **~76 MB extracted, ~33 MB compressed** per platform. Most of that is the .NET 10 runtime and BCL. Trimming with `<PublishTrimmed>true</PublishTrimmed>` removes IL the linker proves is unreachable; in a similar-shape .NET 10 project this typically cuts 30–60% off the binary. For aiboard that's a credible 25–45 MB extracted / 10–20 MB compressed — a meaningful win for users on size-constrained networks or CI pullers.

We've already added a framework-dependent (FDD) build path in v0.0.23 that addresses the size question for users with .NET 10 installed (~5–10 MB compressed). **Trimming is for the self-contained path** — its audience explicitly lacks .NET 10 on the host, so the runtime stays bundled, but unreachable runtime IL can go.

This is **a spike, not a planned migration**. The fundamental risk is silent runtime failures: aiboard's hot paths use heavy reflection (`Microsoft.Extensions.Configuration.Binder`, `System.Text.Json` polymorphic converters, DI activation, options-binding) and trim warnings catch most but not all reflection sites. The decision to merge is data-driven: ≥30% size win and zero functional regressions, or document the blockers.

## Goal

Ship a trimmed self-contained build alongside the existing untrimmed self-contained and framework-dependent builds, **only if** the size win justifies the maintenance cost (source-gen contexts to maintain, suppressions to audit on every dependency bump).

## Acceptance criteria

The spike succeeds and we merge if **all** of the following hold on the spike branch:

1. `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true` produces a binary at least **30% smaller** than the untrimmed equivalent.
2. The full `dotnet test` suite passes against the source (no regressions in non-trimmed test runs).
3. **Smoke test on trimmed binary** passes end-to-end:
   - `aiboard --help` runs.
   - `aiboard --mode validation --board-id 1` against a known-good workflow.json runs.
   - `aiboard --mode metrics` against a populated DB runs.
   - At least one full agent run (`--mode agent --card-id N` against `stub` board provider + `stub` agent executor) completes with `outcome=COMPLETE`.
4. Trim warnings in the build output are either:
   - Eliminated (fixed at the source), OR
   - Suppressed via `[UnconditionalSuppressMessage]` with a comment explaining why the suppressed call is trim-safe.
5. Final unsuppressed warning count is **≤5** and each remaining warning is documented in this file under "Known limitations."

If any of those fail, write up the blocker in this file and shelve the spike.

## Phase 1 — Baseline

1. Create branch `experiment/publish-trimmed`.
2. Add `<PublishTrimmed>true</PublishTrimmed>` to [lambda/src/TaskBoard.Worker/TaskBoard.Worker.csproj](../lambda/src/TaskBoard.Worker/TaskBoard.Worker.csproj). Default trim mode (full).
3. Also add these strictness flags so reflection-heavy patterns fail at build time rather than at runtime:
   - `<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>` — opts into source-generated config binding.
   - `<JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>` — forces every `JsonSerializer` call to use a source-generated `JsonSerializerContext` or fail at build.
4. Run `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true 2>&1 | tee trim-baseline.txt`. Capture the warning list.
5. Record the binary size delta in this file.

Expected first-run state: hundreds of warnings, build may still succeed. Don't try to fix everything at once.

## Phase 2 — Categorize warnings

Group warnings by category. Common ones for aiboard's shape:

| Code | Meaning | Typical source |
|---|---|---|
| **IL2026** | Calling a method marked `[RequiresUnreferencedCode]` | `IConfiguration.Get<T>()`, `JsonSerializer.Deserialize<T>(json)` without a context |
| **IL2070** / **IL2072** / **IL2075** | Parameter / return / field needs `[DynamicallyAccessedMembers]` and the caller doesn't supply one | Custom reflection helpers |
| **IL2104** | Assembly-level dynamic access | Third-party libraries (Npgsql, etc.) |
| **IL3050** | `[RequiresDynamicCode]` — AOT-specific but trim-adjacent | Generic virtual method dispatch in some libraries |

For each grouped warning, decide:
- **Fix at source** (source-gen / refactor) — preferred.
- **Annotate** with `[DynamicallyAccessedMembers]` — when reflection is internal and the type set is bounded.
- **Suppress** with `[UnconditionalSuppressMessage]` — when the call is provably trim-safe but the analyzer can't see it.
- **Root the assembly** with `<TrimmerRootAssembly Include="..." />` — for third-party libraries we can't refactor.

## Phase 3 — Concrete refactor targets

### 3.1 Configuration binding (highest impact)

aiboard binds configuration in many places:

```csharp
services.Configure<DockerOpenCodeAgentOptions>(config.GetSection("DockerAgents:OpenCode"));
config.GetSection("Database").Get<DatabaseOptions>();
```

Both patterns trip IL2026 under trim. **Migrate to the source-gen Binder**:

1. Set `<EnableConfigurationBindingGenerator>true</EnableConfigurationBindingGenerator>` (already in Phase 1).
2. The generator produces a trim-safe binder for any type used in `services.Configure<T>` or `.Get<T>()`. No call-site changes needed for the common pattern — just opt in.
3. Verify by checking `obj/Debug/.../generated/Microsoft.Extensions.Configuration.Binder.SourceGeneration/` after build.

**Option types that bind from config** (incomplete list — audit the codebase):

- `DockerClaudeAgentOptions`
- `DockerCodexAgentOptions`
- `DockerClaudeQwenAgentOptions`
- `DockerOpenCodeAgentOptions`
- `ClaudeCliLlmOptions`
- `CodexCliLlmOptions`
- `GitHubProjectsOptions`
- `TrelloClientOptions`
- `DatabaseOptions`
- `PgmqOptions`
- `StubOptions`
- `WorkflowConfig` (loaded directly via `JsonSerializer`, NOT `IConfiguration` — see Phase 3.2)

**Catch**: the source-gen binder analyzer needs the option type's properties to be **public, settable, and trim-friendly types**. Audit each option class for `init`-only properties or non-trivial setter logic.

### 3.2 JSON serialization (next-highest impact)

aiboard uses `System.Text.Json` for:

- Loading `workflow.github.json` / `workflow.v1.json` / templates' `workflow.json` → `WorkflowConfig`.
- Loading `appsettings.json` (handled by `IConfiguration` framework — covered in 3.1).
- Parsing CLI executor structured-output (`AgentResult`, `EvaluatorScore`, etc.) via `AgentOutputParser`.
- Parsing `gh` CLI command output (`gh project item-list --format json`, `gh issue view --json`, etc.) — every JSON shape gets its own DTO.
- Writing temp JSON files (Codex schema, Claude `--json-schema` arg).

**Migrate every call to source-generated `JsonSerializerContext`**:

1. Create [lambda/src/TaskBoard.Worker/AiboardJsonContext.cs](../lambda/src/TaskBoard.Worker/AiboardJsonContext.cs):

   ```csharp
   [JsonSerializable(typeof(WorkflowConfig))]
   [JsonSerializable(typeof(AgentResult))]
   [JsonSerializable(typeof(EvaluatorScore))]
   [JsonSerializable(typeof(GhProjectItemList))]
   // ... one entry per type that hits JsonSerializer
   internal partial class AiboardJsonContext : JsonSerializerContext { }
   ```

2. Replace every `JsonSerializer.Deserialize<T>(json)` with `JsonSerializer.Deserialize(json, AiboardJsonContext.Default.T)`.
3. Replace every `JsonSerializer.Serialize(obj)` with `JsonSerializer.Serialize(obj, AiboardJsonContext.Default.T)`.

**Polymorphic types** (`TransitionTarget` — string OR action array; `CardFilter` — discriminated union by `type`): these need custom `JsonConverter`s registered on the type via `[JsonConverter(typeof(MyConverter))]`. The converters themselves must use the same generated context internally to deserialize sub-trees.

**Discovery sites** (incomplete — grep for usage):

```bash
grep -rn "JsonSerializer\." lambda/src/TaskBoard.Worker/ --include="*.cs"
```

Expected callers (rough): `WorkflowConfigLoader`, `AgentOutputParser`, `OpenCodeOutputParser`, `CodexOutputParser`, `GitHubProjectsClient` (every `--json` parse), `TaskFileManager` (front-matter parsing — though that may not be JSON), `CardClaimService`, `PgRunStore` (jsonb columns), `MetricsRunner` (output formatting if it uses System.Text.Json).

### 3.3 IOptions activation

Modern .NET (`Microsoft.Extensions.Options`) is mostly trim-safe **as long as** the option type is trim-safe (which Phase 3.1 ensures). Re-verify by building and checking no IL2026 warnings reference `OptionsFactory<T>` or `OptionsManager<T>`.

### 3.4 Polymorphic JSON converters

Specific risk: `WorkflowConfig.TransitionTarget` accepts either a string or an array of action objects. The custom converter probably uses `JsonElement.Deserialize<T>` internally. Audit every custom `JsonConverter` for reflection-based deserialization and migrate to context-aware deserialization.

### 3.5 DI activation via `ActivatorUtilities.CreateInstance`

Search for `ActivatorUtilities.CreateInstance` and similar runtime-DI patterns. These typically need `[DynamicallyAccessedMembers(PublicConstructors)]` annotations on the type parameter, or a refactor to direct `new` plus dependency injection.

### 3.6 Third-party library audit

- **Npgsql** — Npgsql 8+ supports trim/AOT. Verify the version aiboard uses (check `*.csproj` PackageReference) and any required `<TrimmerRootAssembly>` settings or `<TrimMode>` overrides.
- **Microsoft.Extensions.Hosting** — generally trim-safe in .NET 8+.
- **Microsoft.Extensions.Logging** — trim-safe; provider-specific issues with `Microsoft.Extensions.Logging.EventLog` (Windows event log) are possible. Suppress per-call if needed.
- **xunit / NSubstitute** — test-only, not in publish output, irrelevant.

## Phase 4 — Suppression policy

For each `[UnconditionalSuppressMessage]`:

- Inline comment on the same line explaining why the call is trim-safe.
- Preferred: a fixed, known set of types is reflected — list them in the comment.
- Avoid blanket suppressions on classes or namespaces.

Example (good):

```csharp
[UnconditionalSuppressMessage("Trimming", "IL2026",
    Justification = "TaskFileManager only deserializes WorkflowConfig and AgentResult, both registered in AiboardJsonContext.")]
public static T LoadFromFile<T>(string path) where T : class { ... }
```

Example (bad — too broad):

```csharp
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "OK")]
```

## Phase 5 — Validation

1. **Build with `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`** scoped to trim warnings (if possible — use `<WarningsAsErrors>IL2026;IL2070;IL2072;IL2075</WarningsAsErrors>`). This catches regressions before publish.
2. **Run the full test suite**: `dotnet test task-board.sln --configuration Release`. Tests don't trim, but they catch behavior regressions in the refactored types/converters.
3. **Smoke test on the trimmed publish output** (this is the failure mode tests can't catch):
   - `aiboard --help`
   - `aiboard --mode validation --board-id 1` (against a known-good test config)
   - `aiboard --mode metrics`
   - One end-to-end agent run with `BoardProvider=stub` and `AgentExecutor=stub`
   - One real card pickup if convenient
4. **Manual diff** of the publish output — ensure no obviously-needed assemblies got trimmed.

## Phase 6 — Decision and merge

Outcomes:

- **A. ≥30% size win, all acceptance criteria met** — merge. Add a third matrix dimension to [.github/workflows/release.yml](../.github/workflows/release.yml): `deployment: sc-trimmed`. Asset name `aiboard-{rid}-trimmed.{ext}`. Document in QUICKSTART.md as an "advanced" option for users wanting the smallest self-contained binary.
- **B. <30% size win OR criteria not met** — write up the blockers in this file under "Known blockers" with specific warning codes / call sites. Don't merge; revisit after a future .NET version upgrade or library bump.
- **C. Mid-spike abandoned** — write up "what was tried and why it didn't work" so the next attempt doesn't repeat the same paths.

## Risks and gotchas

1. **Silent runtime failures** are the dominant risk. Trim warnings catch ~80% of reflection sites; the remaining 20% surface only at runtime. The smoke-test list in Phase 5 is the primary defense.
2. **`JsonSerializerIsReflectionEnabledByDefault=false`** will break any third-party library that calls `JsonSerializer.Deserialize<T>(json)` without a context. If a dependency does this internally, we need to either work around it (different API surface) or root the assembly (`<TrimmerRootAssembly>`). This may surface late.
3. **Configuration source-gen has rough edges** in some `IConfiguration` extension methods. The generator produces a partial method that the framework calls; if the generator misses one of aiboard's option types (e.g. due to a non-trivial property type), it silently falls back to reflection. Watch for IL2026 warnings on `Get<T>()` / `Configure<T>` after enabling the generator.
4. **Polymorphic JSON converters** are the most likely source of remaining warnings. They handle types the source-gen context doesn't see directly, so the trimmer can't prove safety. Each polymorphic converter likely needs case-by-case attention.
5. **`<TrimmerSingleWarn>false</TrimmerSingleWarn>`** is recommended for the spike — show every warning, not the first per assembly. Helps with categorization.
6. **CI cost**: full publish + smoke test on trimmed binary should be a separate workflow job from the regular release pipeline. Don't gate releases on it during the spike — only after it's stable.
7. **Operator config schema additions become a two-place edit** if the source-gen Binder is in use: add the property to the option class AND verify the generator picked it up. Counter-argument: aiboard's option types are stable; this is rare.

## What this spike is NOT

- Not NativeAOT (`<PublishAot>true</PublishAot>`). That's a much bigger lift (no JIT, no `Assembly.Load`, no dynamic codegen) and warrants its own spike. Trim is a precursor — if trim works, AOT might follow.
- Not partial trimming (`<TrimMode>partial</TrimMode>`). Start with default (`full` / `link`); fall back to partial only if full proves unworkable.
- Not per-platform trimming. Same trim mode for all four release artifacts.
- Not a rewrite of the JSON or config layer. Source-gen is opt-in and additive — existing reflection-based call sites still work; the spike just makes them trim-safe.

## Out of scope but adjacent

- **`PublishReadyToRun`** — pre-JITs the assembly for faster cold start. Independent of trim. Could be added to release.yml separately if cold-start matters.
- **`InvariantGlobalization`** — drops ICU, saves ~10 MB. Strict subset of locales. Probably worth turning on regardless of trim outcome.
- **`EventSourceSupport=false`** — drops EventSource infrastructure. Saves a small amount, may break Application Insights / dotnet-trace integrations. Evaluate based on whether anyone uses those.

## Known blockers (fill in during the spike)

_(empty — populate during Phase 1 / 2)_

## Final results (fill in after the spike)

_(empty — populate at decision time)_

---

## Quick-start for the next agent picking this up

```bash
git checkout -b experiment/publish-trimmed
# Edit lambda/src/TaskBoard.Worker/TaskBoard.Worker.csproj — add the three Phase 1 properties
dotnet publish lambda/src/TaskBoard.Worker -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true 2>&1 | tee trim-baseline.txt
# Inventory warnings, fill in Phase 1 results in this file
```

Read [Microsoft's trim docs](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/trimming-options) before starting — they're the definitive reference for warning codes and remediation patterns.
