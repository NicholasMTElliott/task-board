# Codex Agent Sandbox Docker Image
#
# Provides a minimal, headless environment for running OpenAI Codex CLI
# agents inside a container. The container is the security boundary —
# Codex runs with --yolo by default because filesystem isolation is
# already provided by Docker, not by the CLI's own sandbox layer.
#
# Includes: Codex CLI (npm), git, ripgrep, curl, ca-certificates.
# Runs as non-root `agent` user (UID/GID configurable via build args).
#
# Auth: Codex CLI reads ~/.codex/auth.json (created by `codex login` on
# the host). The DockerCodexMountBuilder copies the host's ~/.codex
# directory into a per-run staging directory and mounts it RW at
# /home/agent/.codex inside the container so the agent inherits the
# operator's session without ever mutating the host directory.
#
# Build args:
#   BASE_IMAGE         - Base Docker image (default: node:22-slim)
#   AGENT_UID          - UID for the non-root agent user (default: 1000)
#   AGENT_GID          - GID for the non-root agent group (default: 1000)
#   CODEX_CLI_VERSION  - npm version spec for @openai/codex (default: latest)

ARG BASE_IMAGE=node:22-slim
FROM ${BASE_IMAGE}

ARG AGENT_UID=1000
ARG AGENT_GID=1000
ARG CODEX_CLI_VERSION=latest

# Install system dependencies:
#   git             - read-only worktree operations the agent uses for
#                     `git status`, `git diff`, `git log`, etc.
#   ripgrep         - expected by coding agents for file search (rg)
#   curl            - common utility; used by Codex tooling and operators
#                     for ad-hoc smoke tests
#   ca-certificates - HTTPS trust anchors for OpenAI API calls
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        git \
        ripgrep \
        curl \
        ca-certificates \
    && rm -rf /var/lib/apt/lists/*

# Remove any existing user at the target UID/GID to avoid conflicts.
# node:22-slim ships a `node` user at UID 1000; delete it before
# creating the `agent` user so the default args work without adjustment.
RUN EXISTING_USER=$(getent passwd "${AGENT_UID}" | cut -d: -f1) \
    && if [ -n "${EXISTING_USER}" ]; then userdel -r "${EXISTING_USER}" 2>/dev/null || true; fi \
    && EXISTING_GROUP=$(getent group "${AGENT_GID}" | cut -d: -f1) \
    && if [ -n "${EXISTING_GROUP}" ]; then groupdel "${EXISTING_GROUP}" 2>/dev/null || true; fi

# Create non-root agent user with configurable UID/GID.
RUN groupadd -g "${AGENT_GID}" agent \
    && useradd -m -u "${AGENT_UID}" -g agent -s /bin/bash agent

# Install Codex CLI globally as root so it lands in /usr/local/bin
# and is accessible to the agent user.
RUN npm install -g "@openai/codex@${CODEX_CLI_VERSION}"

# Create workspace directory owned by agent user.
# This is the default mount target used by the Docker executor.
RUN mkdir -p /workspace && chown agent:agent /workspace

WORKDIR /workspace
USER agent

# No ENTRYPOINT — the executor invokes `codex exec ...` directly so
# both `docker run` (per-step) and `docker exec` (container reuse)
# patterns work without a wrapper.
CMD ["bash"]
