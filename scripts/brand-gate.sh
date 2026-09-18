#!/usr/bin/env bash
# Brand gate (ADR-0001): the upstream MIT copyright line in LICENSE is the only
# permitted upstream reference. Fails on any case-insensitive hit of the upstream
# brand tokens in tracked file CONTENTS or tracked file PATHS.
#
# Exclusions, and why:
#   LICENSE           - the MIT copyright line is legally required (ADR-0001).
#   this script       - it necessarily contains the tokens it searches for.
#   lockfile hashes   - npm/pnpm integrity values are base64 digests that can
#                       contain "fsh" by chance (e.g. "...2BFshejCYXni..."); only
#                       lockfile lines carrying such a digest are skipped, so
#                       package names and URLs in lockfiles are still checked.
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

PATTERN='fsh|fullstackhero|pantryk|mukesh|codewithmukesh'
SELF='scripts/brand-gate.sh'
# Lockfile lines whose only possible match is inside a content-address digest.
DIGEST_LINE='^([^:]*/)?(package-lock\.json|pnpm-lock\.yaml):[0-9]+:.*(integrity|sha512-|sha384-|sha256-|sha1-)'

status=0

content_hits=$(
  git grep -nIiE "$PATTERN" -- . ":(exclude)LICENSE" ":(exclude)$SELF" \
    | grep -vE "$DIGEST_LINE" \
    || true
)

if [ -n "$content_hits" ]; then
  echo "Brand gate: upstream brand tokens found in tracked file contents:"
  echo "$content_hits"
  status=1
fi

path_hits=$(
  git ls-files \
    | grep -iE "$PATTERN" \
    | grep -vxF "$SELF" \
    || true
)

if [ -n "$path_hits" ]; then
  echo "Brand gate: upstream brand tokens found in tracked file paths:"
  echo "$path_hits"
  status=1
fi

if [ "$status" -ne 0 ]; then
  echo
  echo "Rename to the 'Boilerplate' placeholder (ADR-0001) and re-run: bash $SELF"
  exit 1
fi

echo "Brand gate: clean."
