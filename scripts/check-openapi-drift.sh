#!/usr/bin/env bash
# OpenAPI drift gate, both sides (issue #15, ADR-0008). The checked-in contract,
# clients/openapi/v1.json, must be reproducible from the API, and EVERY client's
# generated types must be reproducible from that one contract — this is the single
# place both CI jobs and a developer ask either question.
#
#   bash scripts/check-openapi-drift.sh backend   # re-export the document from the API
#   bash scripts/check-openapi-drift.sh frontend  # regenerate both clients' types
#
# `git status --porcelain`, not `git diff --exit-code`: a regeneration that
# DELETES a file or leaves an untracked one is drift too, and diff --exit-code
# only sees changes to tracked content.
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

MODE=${1:-}

case "$MODE" in
  backend)
    bash scripts/export-openapi.sh
    PATHS=(clients/openapi/)
    FIX="bash scripts/export-openapi.sh"
    ;;
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
  *)
    echo "usage: check-openapi-drift.sh {backend|frontend}" >&2
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
