#!/usr/bin/env bash
# Contract tests for docs/deploy-dokploy.md (D6, deploy-operability research).
#
# The guide is a walkthrough a fresh operator follows literally: if it names
# the wrong number of DNS records, or points the seeded root admin at the
# wrong client's domain, the reader hits a dead end (or the wrong sign-in
# screen) with nothing in the file to say why. These pin the fix rather than
# leaving it to a future edit to notice the drift again.
# The patterns below are single-quoted on purpose: a literal $ or backtick is
# what they match, so nothing in them should expand.
# shellcheck disable=SC2016
set -uo pipefail

TESTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOKPLOY_DIR="$(dirname "$TESTS_DIR")"
REPO_ROOT="$(dirname "$(dirname "$DOKPLOY_DIR")")"

# shellcheck source=deploy/dokploy/tests/lib.sh
. "$TESTS_DIR/lib.sh"

GUIDE="$REPO_ROOT/docs/deploy-dokploy.md"

printf '%s\n' "── docs/deploy-dokploy.md ──"

assert_true "docs/deploy-dokploy.md exists" test -s "$GUIDE"
guide_text="$(cat "$GUIDE")"

# ── D6: the DNS table matches the four (or two, API-only) domain variables ──
# app.compose.yml's Traefik labels route DASHBOARD_DOMAIN and CONSOLE_DOMAIN as
# two separate hostnames (ADR-0008); a reader creating only three records has
# no CONSOLE_DOMAIN to point at.
refute_match "the guide no longer claims three hostnames" "$guide_text" 'Three hostnames'
# The guide templates its frontend and API-only sides, so a scaffold
# keeps only one side: assert each side only where it survives. In the repository
# these directives are plain comments and both sides are checked.
#if (frontend)
assert_match "the frontend guide claims four hostnames" "$guide_text" 'Four hostnames point at the server'
assert_match "the DNS table has its own Dashboard record" "$guide_text" '\| Dashboard \|'
assert_match "the DNS table has its own Console record, on its own hostname" "$guide_text" \
  '\| Console \| `console\.example\.com` \|'
#endif
#if (!frontend)
assert_match "the API-only guide claims two hostnames" "$guide_text" 'Two hostnames point at the server'
#endif
refute_match "the DNS table no longer conflates Console with the web front end" "$guide_text" \
  '\| Console \| `app\.example\.com` \|'

# ── D6: first sign-in is on the Console, not the Dashboard ───────────
# CONTEXT.md is explicit that root-tenant operators sign in at the Console, and
# a tenant user who lands on it is told it is not their app — the two are not
# interchangeable, so the seeded root admin's first sign-in must not point at
# DASHBOARD_DOMAIN's example host.
#if (frontend)
assert_match "first sign-in is on the console's example host" "$guide_text" \
  'sign in at `https://console\.example\.com`'
#endif
refute_match "first sign-in no longer points at the dashboard's example host" "$guide_text" \
  'sign in at `https://app\.example\.com`'
assert_match "the seeded admin is named at first sign-in" "$guide_text" 'admin@root\.com'

# ── D1: the backup and restore runbook exists and names the exact commands ──
# The ticket is done when the runbook gives the exact restore commands, not a
# description of the shape of one — pin the command a reader would actually
# copy-paste, not just the section heading.
assert_match "the guide has a back up and restore section" "$guide_text" \
  '## 11\. Back up and restore Postgres'
assert_match "the runbook names the backup schedule and retention keys" "$guide_text" \
  'BACKUP_SCHEDULE'
assert_match "the runbook names the retention key" "$guide_text" 'BACKUP_KEEP_DAYS'
assert_match "the runbook's restore uses --clean --if-exists --no-owner" "$guide_text" \
  'pg_restore --clean --if-exists --no-owner'
assert_match "the runbook warns to stop the app stack before restoring" "$guide_text" \
  'Stop the app stack first'

summary
