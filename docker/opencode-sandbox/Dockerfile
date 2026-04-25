# OpenCode Agent Sandbox Docker Image
#
# Provides a minimal, headless environment for running OpenCode agents against
# a local Anthropic-compatible llama.cpp server (e.g. Qwen3.6 served by the
# `local-llm` sibling project).
#
# Includes: OpenCode CLI (npm), git, ripgrep, curl, ca-certificates.
# Runs as non-root `agent` user (UID/GID configurable via build args).
#
# Build args:
#   BASE_IMAGE           - Base Docker image (default: node:22-slim)
#   AGENT_UID            - UID for the non-root agent user (default: 1000)
#   AGENT_GID            - GID for the non-root agent group (default: 1000)
#   OPENCODE_CLI_VERSION - npm version spec for opencode-ai (default: latest)

ARG BASE_IMAGE=node:22-slim
FROM ${BASE_IMAGE}

ARG AGENT_UID=1000
ARG AGENT_GID=1000
ARG OPENCODE_CLI_VERSION=latest

# Install system dependencies:
#   git        - required for read-only worktree operations
#   ripgrep    - expected by coding agents for file search (rg)
#   curl       - health-check utility for verifying llama-server reachability
#   ca-certificates - HTTPS trust anchors (llama-server may be behind TLS)
#   gettext-base - provides envsubst for templating config files at runtime
RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        git \
        ripgrep \
        curl \
        ca-certificates \
        gettext-base \
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

# Install OpenCode CLI globally as root so it lands in /usr/local/bin
# and is accessible to the agent user.
# NOTE: at time of writing the canonical package is `opencode-ai`. If upstream
# ever renames this, update OPENCODE_CLI_VERSION and adjust the package name.
RUN npm install -g "opencode-ai@${OPENCODE_CLI_VERSION}"

# Pre-create the agent user's config directory so the entrypoint can
# materialise opencode.json into it at container start.
RUN mkdir -p /home/agent/.config/opencode \
    && chown -R agent:agent /home/agent/.config

# Config template: uses envsubst-style placeholders filled in by the entrypoint
# from env vars (OPENCODE_PROVIDER_BASE_URL / OPENCODE_AUTH_TOKEN /
# OPENCODE_MODEL_NAME). Shape follows OpenCode's documented provider schema
# (custom Anthropic-compatible endpoint). If OpenCode's schema changes, update
# this template — the rest of the executor is schema-agnostic.
COPY opencode.template.json /etc/aiboard/opencode.template.json
COPY entrypoint.sh /usr/local/bin/aiboard-entrypoint
RUN chmod +x /usr/local/bin/aiboard-entrypoint

# Create workspace directory owned by agent user.
# This is the default mount target used by the Docker executor.
RUN mkdir -p /workspace && chown agent:agent /workspace

WORKDIR /workspace
USER agent

# Entrypoint templates the OpenCode config from env vars, then execs the
# command line. Supports both:
#   docker run <image> opencode run ...    (single-step invocation)
#   docker exec <container> opencode ...    (container reuse pattern)
ENTRYPOINT ["/usr/local/bin/aiboard-entrypoint"]
CMD ["bash"]
