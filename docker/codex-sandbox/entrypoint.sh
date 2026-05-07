#!/bin/bash
# Codex sandbox entrypoint.
#
# Writes the operator-supplied $CODEX_INSTALLATION_ID into
# ~/.codex/installation_id so Codex CLI 0.128.0+ reads back a stable UUID
# instead of generating a fresh per-run one. The file is created agent-owned
# (we run as USER agent), so no chmod / permissions tussle inside Codex's
# resolve_installation_id() — see codex-rs/core/src/installation_id.rs.
#
# Background:
#   Codex 0.128.0 always opens installation_id with O_RDWR|O_CREAT, which
#   fails EROFS on a read-only bind mount even though the existing UUID
#   would be reused without writing. Mounting the staged copy RW instead
#   yielded EPERM on the chmod-to-0o644 path because Docker Desktop
#   bind-mounts surface as root-owned. The cleanest fix is to skip the
#   bind mount entirely: aiboard generates a stable per-host UUID, passes
#   it via env var, and this entrypoint writes it into an agent-owned
#   regular file before codex starts.
#
# Skipped silently when:
#   - $CODEX_INSTALLATION_ID is unset or empty (test runs, manual debug)
#   - The value isn't a syntactically plausible UUID (defence in depth —
#     codex would otherwise rewrite an invalid value)
set -e

if [ -n "${CODEX_INSTALLATION_ID:-}" ]; then
    # Loose UUID shape check: 8-4-4-4-12 hex blocks. Codex's parser is
    # stricter (validates the variant + version bits) but rejecting an
    # obviously-malformed value here gives clearer logs than letting
    # codex silently rewrite it.
    if [[ "$CODEX_INSTALLATION_ID" =~ ^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$ ]]; then
        printf '%s' "$CODEX_INSTALLATION_ID" > "$HOME/.codex/installation_id"
    else
        echo "aiboard-codex entrypoint: CODEX_INSTALLATION_ID is set but not a valid UUID; ignoring" >&2
    fi
fi

exec "$@"
