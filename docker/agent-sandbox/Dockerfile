# Agent Sandbox Docker Image
#
# Provides a minimal, headless environment for running Claude Code agents.
# Includes: Claude CLI (npm), git, ripgrep, curl.
# Runs as non-root `agent` user (UID/GID configurable via build args).
#
# Build args:
#   BASE_IMAGE        - Base Docker image (default: node:22-slim)
#   AGENT_UID         - UID for the non-root agent user (default: 1000)
#   AGENT_GID         - GID for the non-root agent group (default: 1000)
#   CLAUDE_CLI_VERSION - npm version spec for @anthropic-ai/claude-code.
#                        Default is pinned to a specific version we have
#                        validated against — bleeding-edge defaults bit us
#                        repeatedly with silent wire-shape changes (see
#                        memory-bank/systemPatterns.md "Codex Executor Defensive
#                        Diagnostics" and the eve issue #9 ~/.claude.json
#                        regression). Bumping requires the version-pin dance
#                        documented in docs/CliVersionTesting.md: capture
#                        fixture, run parser-corpus tests, bump
#                        CliVersionPolicy.MaxKnown, then bump THIS default.
#                        The DockerfilePolicyDriftTests guard fails CI if
#                        this default drifts ahead of CliVersionPolicy.

ARG BASE_IMAGE=node:22-slim
FROM ${BASE_IMAGE}

ARG AGENT_UID=1000
ARG AGENT_GID=1000
ARG CLAUDE_CLI_VERSION=2.1.126

# Install system dependencies:
#   git             - required for worktree operations
#   ripgrep         - required by Claude Code for file search (rg)
#   curl            - common utility; used by Claude Code tooling
#   ca-certificates - HTTPS trust anchors. Required by curl when project
#                     overlays (FROM aiboard-agent-sandbox:latest) fetch
#                     their own dependencies over HTTPS, and matches what
#                     the Codex/OpenCode sandboxes already ship.
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        git \
        ripgrep \
        curl \
        ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Remove any existing user at the target UID/GID to avoid conflicts.
# node:22-slim ships a `node` user at UID 1000; we delete it before
# creating the `agent` user so the default args work without adjustment.
RUN EXISTING_USER=$(getent passwd "${AGENT_UID}" | cut -d: -f1) \
    && if [ -n "${EXISTING_USER}" ]; then userdel -r "${EXISTING_USER}" 2>/dev/null || true; fi \
    && EXISTING_GROUP=$(getent group "${AGENT_GID}" | cut -d: -f1) \
    && if [ -n "${EXISTING_GROUP}" ]; then groupdel "${EXISTING_GROUP}" 2>/dev/null || true; fi

# Create non-root agent user with configurable UID/GID.
RUN groupadd -g "${AGENT_GID}" agent \
    && useradd -m -u "${AGENT_UID}" -g agent -s /bin/bash agent

# Install Claude CLI globally as root so it lands in /usr/local/bin
# and is accessible to the agent user.
RUN npm install -g "@anthropic-ai/claude-code@${CLAUDE_CLI_VERSION}"

# Create workspace directory owned by agent user.
# This is the default mount target used by the Docker executor.
RUN mkdir -p /workspace && chown agent:agent /workspace

WORKDIR /workspace
USER agent

# No ENTRYPOINT — supports both:
#   docker run <image> claude --print ...   (single-step invocation via #63)
#   docker exec <container> claude ...      (container reuse pattern via #62)
CMD ["bash"]
