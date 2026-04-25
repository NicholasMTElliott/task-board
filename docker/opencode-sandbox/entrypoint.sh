#!/usr/bin/env bash
# Entrypoint for aiboard-opencode-sandbox.
#
# Responsibilities:
#   1. Validate the three connection env vars are present.
#   2. Template /etc/aiboard/opencode.template.json into
#      ~/.config/opencode/opencode.json using envsubst (so the provider
#      config for each container run reflects the current env, and no
#      secrets are baked into the image layers).
#   3. exec the provided command (opencode, bash, whatever docker run specified).
#
# Stays quiet on success. Fails loudly on missing env vars — silent failure
# here would produce a hard-to-diagnose "agent works but talks to the wrong
# server" bug.
set -euo pipefail

: "${OPENCODE_PROVIDER_BASE_URL:?OPENCODE_PROVIDER_BASE_URL must be set by the executor (e.g. http://llama-server:8080)}"
: "${OPENCODE_AUTH_TOKEN:?OPENCODE_AUTH_TOKEN must be set by the executor (dummy token is fine for llama.cpp)}"
: "${OPENCODE_MODEL_NAME:?OPENCODE_MODEL_NAME must be set by the executor (e.g. qwen3.6-35b-a3b)}"

CONFIG_DIR="${HOME}/.config/opencode"
CONFIG_FILE="${CONFIG_DIR}/opencode.json"
TEMPLATE="/etc/aiboard/opencode.template.json"

mkdir -p "${CONFIG_DIR}"

# envsubst replaces $VAR / ${VAR} literals. We restrict substitution to our
# three known vars so any other dollar signs in the template (there shouldn't
# be any, but defence in depth) pass through untouched.
envsubst '${OPENCODE_PROVIDER_BASE_URL} ${OPENCODE_AUTH_TOKEN} ${OPENCODE_MODEL_NAME}' \
    < "${TEMPLATE}" > "${CONFIG_FILE}"

# No args → drop into bash (interactive debugging from `docker exec`).
if [ "$#" -eq 0 ]; then
    exec bash
fi

exec "$@"
