# Project Sandbox Overlays

Add project-specific tooling on top of the aiboard sandbox images without forking them.

If your project's agent work needs a runtime that the upstream sandbox doesn't ship — a game engine, a JVM, a specific compiler, a CLI tool — you have three options:

| Option | Cost | Verdict |
|---|---|---|
| Fork the upstream `Dockerfile` into your project | Every aiboard release that touches the sandbox (CLI bumps, base-image swaps, security patches) is a manual merge. | Brittle. Avoid. |
| Install the runtime at agent-run time | Every card pays the install cost; multiple steps compete for IO; the install is invisible to aiboard's container reuse. | Slow. Avoid. |
| **Overlay the upstream image** | One small `Dockerfile` per executor family in your project repo; rebuild only when upstream or your tooling changes. | Recommended. |

This doc describes option 3.

---

## 1. The pattern

Your project ships a tiny `Dockerfile` that does `FROM aiboard-X-sandbox:latest` + adds the project-specific tooling, retags as `aiboard-X-sandbox:<your-tag>` (e.g. `:godot`, `:jvm21`, `:rust1.84`), and wires that tag in via `DockerAgents:*:ImageName` in your project's `.aiboard/appsettings.json`.

aiboard's executors don't care what's in the image as long as the agent CLI is on PATH and the `agent` user (UID 1000) is the runtime user — both inherited from the upstream base. Everything else you add is yours.

If the project tooling itself runs Docker, split the work in two places:

- Overlay image: install Docker CLI / Compose.
- Runtime config: set `DockerAgents:<Provider>:MountHostDockerSocket = true` so the sandbox can reach the host daemon.

A Dockerfile cannot grant daemon access by itself. The socket mount is a runtime `docker run -v /var/run/docker.sock:/var/run/docker.sock` decision and gives the agent host-Docker control. If the sandbox user cannot read/write the socket, keep the runtime user as `agent` and add the socket's group id with `DockerAgents:<Provider>:GroupAdd`. Do not set `ContainerUser` to `root`; Claude CLI rejects bypass-permissions mode under root/sudo, and other CLIs can drift to `/root` for credentials.

---

## 2. Layout

In your project repo:

```
docker/agent-sandbox/
  Dockerfile.claude-<addon>       # FROM aiboard-agent-sandbox:latest    + your tools
  Dockerfile.codex-<addon>        # FROM aiboard-codex-sandbox:latest    + your tools
  Dockerfile.opencode-<addon>     # FROM aiboard-opencode-sandbox:latest + your tools
  build-claude-<addon>.ps1
  build-codex-<addon>.ps1
  build-opencode-<addon>.ps1
  .dockerignore                   # excludes everything (overlays use no COPY)
```

One `Dockerfile` + one build script per executor family you use. The directory lives under `docker/` (committed, project-local), not under `.aiboard/` (which is conventionally gitignored except for the two config files).

> **Naming asymmetry to watch.** The three upstream images use three different name stems:
> - Claude:   `aiboard-agent-sandbox:latest`
> - Codex:    `aiboard-codex-sandbox:latest`
> - OpenCode: `aiboard-opencode-sandbox:latest`
>
> Don't pattern-match `aiboard-claude-sandbox` — it doesn't exist. The Claude image predates the multi-CLI naming convention.

---

## 3. Dockerfile template

```dockerfile
FROM aiboard-<executor>-sandbox:latest

USER root

ARG MY_TOOL_VERSION=1.2.3

RUN apt-get update \
    && apt-get install -y --no-install-recommends <packages-you-need> \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL -o /tmp/tool.tar.gz "https://example.com/tool-${MY_TOOL_VERSION}.tar.gz" \
    && tar -xzf /tmp/tool.tar.gz -C /usr/local/bin/ \
    && chmod +x /usr/local/bin/tool \
    && rm /tmp/tool.tar.gz

USER agent
```

Key points:

- **`USER root` to install, then `USER agent` at the end.** All upstream sandboxes run as the non-root `agent` user (UID 1000). Switching to root for installs and switching back at the end keeps the runtime user matching what aiboard's executors expect. Don't leave the image on `USER root`.
- **`--no-install-recommends`** keeps the image small.
- **No `COPY`** in the simplest case — the build context can stay empty. Hence the `*` line in `.dockerignore`.
- **Pin versions via `ARG`.** Reproducibility matters; defaulting to `latest` for your own dependencies hides drift.

---

## 4. Build-script template

```powershell
#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

docker image inspect aiboard-<executor>-sandbox:latest *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Base image 'aiboard-<executor>-sandbox:latest' not found. Build it first via aiboard's scripts/build-<executor>-sandbox.ps1."
    exit 1
}

docker build `
    --tag aiboard-<executor>-sandbox:<your-tag> `
    --file (Join-Path $here 'Dockerfile.<executor>-<addon>') `
    $here

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
docker image ls aiboard-<executor>-sandbox
```

The base-image-existence check catches the most common operator mistake (running the project overlay before upstream is built) and points at the right upstream script.

---

## 5. Wire it into `.aiboard/appsettings.json`

```json
{
  "DockerAgents": {
    "Claude":     { "ImageName": "aiboard-agent-sandbox:<your-tag>" },
    "ClaudeQwen": { "ImageName": "aiboard-agent-sandbox:<your-tag>" },
    "OpenCode":   { "ImageName": "aiboard-opencode-sandbox:<your-tag>" },
    "Codex":      { "ImageName": "aiboard-codex-sandbox:<your-tag>" }
  }
}
```

`Claude` and `ClaudeQwen` both target the same image because aiboard's existing `DockerAgents:Claude` and `DockerAgents:ClaudeQwen` sections share the Claude sandbox image — your overlay inherits that pattern.

You only need entries for executor families your workflow actually references; an unused section can be omitted.

---

## 6. Operator workflow

```powershell
# When upstream changes (aiboard release, base-image bump, CLI version change):
cd <aiboard-source-or-install>
.\scripts\build-sandbox.ps1            # or build-codex-sandbox.ps1 / build-opencode-sandbox.ps1

# Rebuild the project-specific overlay:
cd <your-project-repo>
.\docker\agent-sandbox\build-claude-<addon>.ps1
# (Repeat per family the project uses.)
```

The overlay rebuild after an upstream rebuild is fast — only your project's own apt installs + downloads run. BuildKit caches the layers on subsequent rebuilds where only upstream changed.

---

## 7. Worked example — Godot 4.6.2 on the Claude sandbox

`docker/agent-sandbox/Dockerfile.claude-godot`:

```dockerfile
FROM aiboard-agent-sandbox:latest

USER root

ARG GODOT_VERSION=4.6.2

RUN apt-get update \
    && apt-get install -y --no-install-recommends unzip libfontconfig1 \
    && rm -rf /var/lib/apt/lists/*

RUN curl -fsSL -o /tmp/godot.zip \
        "https://github.com/godotengine/godot/releases/download/${GODOT_VERSION}-stable/Godot_v${GODOT_VERSION}-stable_linux.x86_64.zip" \
    && unzip -q /tmp/godot.zip -d /tmp/godot \
    && mv "/tmp/godot/Godot_v${GODOT_VERSION}-stable_linux.x86_64" /usr/local/bin/godot \
    && chmod +x /usr/local/bin/godot \
    && rm -rf /tmp/godot /tmp/godot.zip

USER agent
```

`docker/agent-sandbox/build-claude-godot.ps1`:

```powershell
#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path

docker image inspect aiboard-agent-sandbox:latest *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Error "Base image 'aiboard-agent-sandbox:latest' not found. Build it first via aiboard's scripts/build-sandbox.ps1."
    exit 1
}

docker build `
    --tag aiboard-agent-sandbox:godot `
    --file (Join-Path $here 'Dockerfile.claude-godot') `
    $here

if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
docker image ls aiboard-agent-sandbox
```

`.aiboard/appsettings.json`:

```json
{
  "DockerAgents": {
    "Claude":     { "ImageName": "aiboard-agent-sandbox:godot" },
    "ClaudeQwen": { "ImageName": "aiboard-agent-sandbox:godot" }
  }
}
```

---

## 8. Caveats

**HTTPS via `curl` works on all upstream images.** All three sandboxes (Claude, Codex, OpenCode) ship `ca-certificates`, so `curl -fsSL https://...` from your overlay's `RUN` works without extra packages. (Earlier Claude sandbox builds — pre-November-2026 — shipped without `ca-certificates`; if you're overlaying onto an older image and hit `curl: (77) error setting certificate file`, either rebuild the upstream image or add `ca-certificates` to your overlay's apt install.)

**Stay on `agent` UID 1000.** The upstream images create the `agent` user at UID 1000 and aiboard's mount builders assume that UID. If you change it (e.g. via a `RUN useradd` for a different user), worktree mounts get permission errors.

**Keep the workspace mount untouched.** `WORKDIR /workspace` and the worktree bind mount are how aiboard threads the per-run worktree into the container. Don't `WORKDIR` somewhere else.

**Heavy tools belong here, not in `RUN` calls per agent run.** If you find yourself tempted to install a runtime in a prompt or `pre-task` hook, that's the signal to overlay it instead — every agent run otherwise pays the install cost and runs serially.
