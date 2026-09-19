#!/usr/bin/env bash
# Run every Dokploy deployment test.
#
#   bash deploy/dokploy/tests/run.sh
#
# No network, no Docker daemon required (the `docker compose config` checks skip
# themselves when docker is absent) and no package to install beyond `jq`, which
# the deploy script needs anyway. Each suite runs in its own process so one
# failure cannot hide another.
set -uo pipefail

TESTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

status=0
for suite in "$TESTS_DIR"/*.test.sh; do
  bash "$suite" || status=1
  printf '\n'
done

if [ "$status" -ne 0 ]; then
  printf 'deploy/dokploy tests: FAILED\n'
else
  printf 'deploy/dokploy tests: all suites passed\n'
fi
exit "$status"
