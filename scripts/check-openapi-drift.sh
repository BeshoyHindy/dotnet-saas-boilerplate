#!/usr/bin/env bash
#if (frontend)
# OpenAPI drift gate, both sides (issue #15, ADR-0008). The checked-in contract,
# clients/openapi/v1.json, must be reproducible from the API, and EVERY client's
# generated types must be reproducible from that one contract — this is the single
# place both CI jobs and a developer ask either question.
#
#   bash scripts/check-openapi-drift.sh backend   # re-export the document from the API
#   bash scripts/check-openapi-drift.sh frontend  # regenerate both clients' types
#else
# OpenAPI drift gate. The checked-in contract, clients/openapi/v1.json, must be
# reproducible from the API: it is the one agreed description of the API, so a change
# to the surface shows up in review as a diff to it. CI's `openapi-drift` job and a
# developer ask the same question here.
#
#   bash scripts/check-openapi-drift.sh backend   # re-export the document from the API
#endif
#
# `git status --porcelain`, not `git diff --exit-code`: a regeneration that
# DELETES a file or leaves an untracked one is drift too, and diff --exit-code
# only sees changes to tracked content.
set -euo pipefail

# Resolve the repository root, and refuse to run outside one: `cd ""` is a no-op, so without this
# check a fresh, not-yet-initialised scaffold would carry on in whatever directory it was run from.
ROOT=$(git rev-parse --show-toplevel 2>/dev/null) || {
  echo "$(basename "$0"): not inside a git repository. Run 'git init -b develop' at the project root first (docs/new-project-guide.md §2)." >&2
  exit 1
}
cd "$ROOT"

MODE=${1:-}

case "$MODE" in
  backend)
    bash scripts/export-openapi.sh
    PATHS=(clients/openapi/)
    FIX="bash scripts/export-openapi.sh"
    ;;
#if (frontend)
  frontend)
    # Both clients generate from the same checked-in document, so both are asked.
    # A client whose schema.d.ts is stale is drift even if the other one is current.
    PATHS=(clients/openapi/)
    for client in dashboard console; do
      (cd "clients/${client}" && pnpm generate:api)
      PATHS+=("clients/${client}/src/api/schema.d.ts")
    done
    FIX="pnpm generate:api (in clients/dashboard and clients/console)"
    ;;
#endif
  *)
    usage="backend"
#if (frontend)
    usage="{backend|frontend}"
#endif
    echo "usage: check-openapi-drift.sh ${usage}" >&2
    exit 2
    ;;
esac

STATUS=$(git status --porcelain -- "${PATHS[@]}")
if [ -n "$STATUS" ]; then
  echo "::error::OpenAPI contract drift detected (${MODE}). Run '${FIX}' and commit the result."
  echo "$STATUS"
  git --no-pager diff -- "${PATHS[@]}"
  exit 1
fi

echo "No OpenAPI drift (${MODE})."
