# CLI Version Testing

aiboard's runtime depends on three external CLIs: **Codex** (`codex exec --json`), **Claude Code** (`claude --output-format stream-json`), and **OpenCode** (free-form stdout). Each CLI is on its own release cadence, and a wire-shape change in any of them can silently break our parsers — that's exactly what happened with Codex CLI 0.125.0, which dropped top-level `structured_output` events in favour of `agent_message` items inside `item.completed` events.

This document describes the three layers of defence we maintain against that class of breakage and how to extend them when a new CLI version drops.

---

## The three layers

### 1. `CliVersionPolicy.KnownGood` — single source of truth

Located at [lambda/src/TaskBoard.Worker/Validation/CliVersionPolicy.cs](../lambda/src/TaskBoard.Worker/Validation/CliVersionPolicy.cs). One dictionary entry per CLI:

```csharp
[CliKey.Codex] = new(MinSupported: new SemVer(0, 125, 0), MaxKnown: new SemVer(0, 125, 0)),
```

- `MinSupported` — oldest CLI version this build's parser handles. Below this, the runtime errors at startup ("upgrade the CLI before continuing") and the operator must upgrade to proceed.
- `MaxKnown` — newest CLI version we've captured a stdout fixture for. Above this, the runtime warns loudly at startup ("you're on uncharted ground; capture a fixture if it works"). The run still proceeds.
- Both `null` means the policy is unpinned; the runtime logs the version at Info but enforces nothing. Used for CLIs (currently Claude and OpenCode) where we haven't yet committed to a tested range.

Consumed by both `CliVersionStartupCheck` (runtime) and `*ParserCorpusTests.EveryFixtureVersion_IsAcknowledgedByPolicy` (regression guard against fixture/policy drift).

### 2. Fixture corpus tests — always-on parser regression coverage

Each shipped CLI version we've ever tested against has a stdout fixture under [lambda/tests/TaskBoard.Worker.Tests/Fixtures/Cli/{cli}/{version}/](../lambda/tests/TaskBoard.Worker.Tests/Fixtures/Cli/). The fixture is a real (or realistic) capture of the CLI's stdout for one structured-output scenario.

Test classes [`CodexParserCorpusTests`](../lambda/tests/TaskBoard.Worker.Tests/Clients/CodexParserCorpusTests.cs), [`ClaudeParserCorpusTests`](../lambda/tests/TaskBoard.Worker.Tests/Clients/ClaudeParserCorpusTests.cs), and [`OpenCodeParserCorpusTests`](../lambda/tests/TaskBoard.Worker.Tests/Clients/OpenCodeParserCorpusTests.cs) auto-discover every fixture file via `CliFixtureLoader.EnumerateFixtures(cli)` and run a `[Theory]` case per file. Each case loads the fixture, runs the parser, and asserts a structured outcome was extracted.

**The whole point**: when a future CLI version changes the wire shape, capture a sample of its stdout into a new `{version}/` directory. If parser-corpus tests pass, you have permanent regression coverage for that shape. If they fail, fix the parser first and add the new fixture to pin the new shape.

### 3. Live smoke tests — pre-release version validation

[`CodexAgentExecutorLiveTests`](../lambda/tests/TaskBoard.Worker.Tests/Clients/CodexAgentExecutorLiveTests.cs) and [`ClaudeAgentExecutorLiveTests`](../lambda/tests/TaskBoard.Worker.Tests/Clients/ClaudeAgentExecutorLiveTests.cs) invoke the real installed CLI against a trivial prompt and assert the parser extracts an outcome. These confirm the *currently installed* CLI version is end-to-end compatible — the layer that catches "my Codex CLI auto-updated yesterday and now production is broken."

Skip-by-default. Enable with:

```bash
AIBOARD_TEST_LIVE_CLI=1 dotnet test --filter "FullyQualifiedName~LiveTests"
```

OpenCode does not have a live test because it runs inside the docker-opencode sandbox image against a local llama.cpp server; the prerequisites (Docker daemon + `llm-net` network + a running `llama-server`) are heavy enough that the existing [`scripts/smoke-opencode.ps1`](../scripts/smoke-opencode.ps1) is the better entry point.

---

## Workflow: a new CLI version dropped

When you upgrade Codex / Claude / OpenCode, follow this sequence.

### Step 1: Capture a stdout fixture

Run the CLI manually with the same flags aiboard uses, and save the stdout to a file under the test fixtures tree.

**Codex CLI** (substitute `{NEW_VERSION}` with the version reported by `codex --version`):

```bash
mkdir -p lambda/tests/TaskBoard.Worker.Tests/Fixtures/Cli/codex/{NEW_VERSION}
echo "Respond with outcome=COMPLETE and detail='captured-fixture'." | \
  codex exec --json --output-schema /tmp/schema.json - \
  > lambda/tests/TaskBoard.Worker.Tests/Fixtures/Cli/codex/{NEW_VERSION}/agent-message-complete.ndjson
```

Where `/tmp/schema.json` is a copy of `AgentSchemas.OutcomeSchemaOpenAI` minified onto a single line.

**Claude CLI** (substitute `{NEW_VERSION}` with the version reported by `claude --version`):

```bash
mkdir -p lambda/tests/TaskBoard.Worker.Tests/Fixtures/Cli/claude/{NEW_VERSION}
echo "Respond with outcome=COMPLETE and detail='captured-fixture'." | \
  claude --print --verbose --output-format stream-json --no-session-persistence \
    --append-system-prompt-file system.md --json-schema "$(cat schema-minified.json)" \
    --permission-mode bypassPermissions --allowedTools '*' \
  > lambda/tests/TaskBoard.Worker.Tests/Fixtures/Cli/claude/{NEW_VERSION}/stream-json-complete.ndjson
```

**OpenCode CLI**: capture stdout from a manual `opencode run` invocation against your local llama-server. Filename should describe the parser strategy exercised (`fenced-json`, `whole-document`, `trailing-json`).

### Step 2: Run the parser-corpus tests

```bash
dotnet test --filter "FullyQualifiedName~ParserCorpusTests"
```

If they pass, the parser already handles the new shape — proceed to step 3.

If they fail, the parser needs an update. Fix it, then re-run until green. The new fixture is now permanently regression-pinned.

### Step 3: Bump `CliVersionPolicy.KnownGood`

In [CliVersionPolicy.cs](../lambda/src/TaskBoard.Worker/Validation/CliVersionPolicy.cs), update the relevant entry:

```csharp
[CliKey.Codex] = new(
    MinSupported: new SemVer(0, 125, 0),
    MaxKnown:     new SemVer(0, 126, 0)),  // bumped
```

Bump `MinSupported` only when a NEW version drops support for an old wire shape that this build no longer parses (rare, but Codex 0.125.0 was an example).

Bump `MaxKnown` whenever you've validated a new version with the parser-corpus tests.

### Step 4 (recommended): Run the live smoke test

```bash
AIBOARD_TEST_LIVE_CLI=1 dotnet test --filter "FullyQualifiedName~LiveTests"
```

End-to-end confirmation that the installed CLI + the parser + the version policy all line up. Costs a few cents in LLM API calls; worth running before a release.

### Step 5: Commit

```
test(cli-version): pin Codex CLI {NEW_VERSION}

Adds Fixtures/Cli/codex/{NEW_VERSION}/agent-message-complete.ndjson and
bumps CliVersionPolicy.KnownGood[codex].MaxKnown. Parser-corpus tests
green; live smoke test green against {NEW_VERSION}.
```

---

## What the runtime does at startup

`Program.cs` calls `CliVersionStartupCheck.CheckAsync` once per detected CLI provider after the executable path is resolved:

| Status      | Severity | Behaviour                                                              |
|-------------|----------|------------------------------------------------------------------------|
| `Old`       | Error    | Startup aborts; operator must upgrade or downgrade the CLI.            |
| `Newer`     | Warning  | Loud log; startup proceeds. Operator should capture a fixture.         |
| `Supported` | Info     | Quiet "OK" log; startup proceeds.                                      |
| `Unknown`   | Info     | Logged as informational (no policy pinned, or version string opaque).  |

Probe failures (`(unknown: ...)` from `TryGetVersionAsync`) log a Warning and proceed — an unrecorded version is worse than no version check at all, but not worth blocking on.
