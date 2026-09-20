#!/usr/bin/env bash
# Tests for deploy/dokploy/dokploy-deploy.sh against a stubbed `curl`.
#
# The point of the script is that it fails closed, so the interesting cases are
# all the ways a deployment can NOT succeed: a rejected trigger, a failed or
# cancelled deployment, a status the script has never heard of, a body that is
# not JSON, a deployment that never finishes, and — the one a naive "poll the
# newest deployment" implementation gets wrong — somebody else's deployment
# going green while ours is still running.
#
# No network is touched: a fake `curl` earlier on PATH answers from a scripted
# sequence of statuses.
set -uo pipefail

TESTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOKPLOY_DIR="$(dirname "$TESTS_DIR")"
SCRIPT="$DOKPLOY_DIR/dokploy-deploy.sh"

# shellcheck source=deploy/dokploy/tests/lib.sh
. "$TESTS_DIR/lib.sh"

printf '%s\n' "── dokploy-deploy.sh ──"

assert_true "dokploy-deploy.sh exists" test -s "$SCRIPT"
assert_true "dokploy-deploy.sh is executable" test -x "$SCRIPT"

if ! command -v jq >/dev/null 2>&1; then
  skip "dokploy-deploy.sh behaviour (jq is not installed here)"
  summary
  exit $?
fi

WORK="$(mktemp -d -t dokploy-deploy-tests.XXXXXX)"
trap 'rm -rf "$WORK"' EXIT
STUB_BIN="$WORK/bin"
mkdir -p "$STUB_BIN"

# ── The curl stub ────────────────────────────────────────────────────
# Answers POST /api/compose.deploy and GET /api/deployment.allByCompose. The
# statuses it reports come from STUB_SEQUENCE, one word per poll, the last word
# repeating forever. It echoes back the correlation token the script sent, which
# is how "foreign" (uncorrelated) deployments can be simulated.
cat > "$STUB_BIN/curl" <<'STUB'
#!/usr/bin/env bash
set -uo pipefail
cat > /dev/null   # drain the --config payload carrying the API key

url=""
data=""
prev=""
for arg in "$@"; do
  case "$prev" in --data) data="$arg" ;; esac
  case "$arg" in http*) url="$arg" ;; esac
  prev="$arg"
done

state="${STUB_STATE:?STUB_STATE is required}"

emit() { printf '%s\n%s' "$1" "$2"; }   # body, then the --write-out status code

if [[ "$url" == *compose.deploy* ]]; then
  printf '%s' "$data" > "$state/payload.json"
  emit "${STUB_DEPLOY_BODY:-true}" "${STUB_DEPLOY_CODE:-200}"
  exit 0
fi

# GET deployment.allByCompose
count_file="$state/polls"
count=$(cat "$count_file" 2>/dev/null || echo 0)
count=$((count + 1))
printf '%s' "$count" > "$count_file"

read -r -a sequence <<< "${STUB_SEQUENCE:-done}"
index=$((count - 1))
[ "$index" -lt "${#sequence[@]}" ] || index=$(( ${#sequence[@]} - 1 ))
step="${sequence[$index]}"

title=$(jq -r '.title' < "$state/payload.json")

case "$step" in
  absent)       body='[]' ;;
  malformed)    body='<html>502 Bad Gateway</html>' ;;
  not-an-array) body='{"code":"NOT_FOUND","message":"Not found","issues":[]}' ;;
  foreign-done) body=$(jq -nc --arg t "$title" '[{title:"Somebody else [dpl-other]",status:"done",createdAt:"2026-01-01"},{title:$t,status:"running",createdAt:"2026-01-02"}]') ;;
  *)            body=$(jq -nc --arg t "$title" --arg s "$step" '[{title:$t,status:$s,errorMessage:"compose up exited 1",createdAt:"2026-01-02"}]') ;;
esac

emit "$body" "${STUB_LIST_CODE:-200}"
STUB
chmod +x "$STUB_BIN/curl"

# Built at run time rather than written down: a credential-shaped literal in the
# repository is exactly what `gitleaks dir .` is meant to stop, even a fake one.
# The prefix keeps it distinctive enough that the "never leaks it" assertions
# cannot pass by accident.
API_KEY="not-a-real-dokploy-key-$$"

# Per-case knobs, reset by `run` so one case can never leak into the next.
SEQ="done"; TIMEOUT=5; INTERVAL=1; DEPLOY_CODE=200; DEPLOY_BODY="true"; LIST_CODE=200

# run <label> — runs the script with the stub first on PATH.
# Sets RUN_OUTPUT and RUN_STATUS.
run() {
  local state="$WORK/state-$1"
  rm -rf "$state"; mkdir -p "$state"
  RUN_OUTPUT="$(
    PATH="$STUB_BIN:$PATH" STUB_STATE="$state" \
    STUB_SEQUENCE="$SEQ" STUB_DEPLOY_CODE="$DEPLOY_CODE" \
    STUB_DEPLOY_BODY="$DEPLOY_BODY" STUB_LIST_CODE="$LIST_CODE" \
    DOKPLOY_URL="https://dokploy.example.test" \
    DOKPLOY_COMPOSE_ID="cmp_123" \
    DOKPLOY_API_KEY="$API_KEY" \
    "$SCRIPT" --interval "$INTERVAL" --timeout "$TIMEOUT" 2>&1
  )"
  RUN_STATUS=$?
  SEQ="done"; TIMEOUT=5; INTERVAL=1; DEPLOY_CODE=200; DEPLOY_BODY="true"; LIST_CODE=200
}

# ── Happy path ───────────────────────────────────────────────────────
SEQ="running done"; run success
assert_eq "a completed deployment exits 0" "0" "$RUN_STATUS"
assert_contains "success is reported" "$RUN_OUTPUT" "finished successfully"
refute_contains "the API key never reaches the output" "$RUN_OUTPUT" "$API_KEY"

# The first poll happens before the first sleep: a budget shorter than one
# interval must still be able to observe an already-finished deployment. This
# run would block for a minute, and then fail, if the sleep came first.
SEQ="done"; TIMEOUT=1; INTERVAL=60; run polls-before-sleeping
assert_eq "an already-finished deployment is seen without waiting an interval" "0" "$RUN_STATUS"

# ── Failure paths — every one of these must exit non-zero ────────────
SEQ="error"; run failed
assert_true "a failed deployment exits non-zero" test "$RUN_STATUS" -ne 0
assert_contains "the failure names the status" "$RUN_OUTPUT" "status 'error'"
assert_contains "the failure surfaces Dokploy's message" "$RUN_OUTPUT" "compose up exited 1"
refute_contains "the API key never reaches a failure message" "$RUN_OUTPUT" "$API_KEY"

SEQ="cancelled"; run cancelled
assert_true "a cancelled deployment exits non-zero" test "$RUN_STATUS" -ne 0
assert_contains "the cancellation is named" "$RUN_OUTPUT" "status 'cancelled'"

SEQ="running"; TIMEOUT=1; run timeout
assert_true "a deployment that never finishes exits non-zero" test "$RUN_STATUS" -ne 0
assert_contains "the timeout is named" "$RUN_OUTPUT" "timed out"

SEQ="absent"; TIMEOUT=1; run never-registered
assert_true "a deployment that is never registered exits non-zero" test "$RUN_STATUS" -ne 0
assert_contains "the missing deployment times out" "$RUN_OUTPUT" "timed out"

SEQ="malformed"; run malformed
assert_true "an unparseable response exits non-zero" test "$RUN_STATUS" -ne 0
assert_contains "the parse failure is named" "$RUN_OUTPUT" "could not parse"

# A JSON object rather than the documented array — an error payload, a proxy's
# own answer. Without a shape check this reads as "no deployment yet" and only
# surfaces as a timeout, which hides the real cause.
SEQ="not-an-array"; run wrong-shape
assert_true "a non-array deployment list exits non-zero" test "$RUN_STATUS" -ne 0
assert_contains "the wrong shape is named" "$RUN_OUTPUT" "expected an array"
refute_contains "a non-array answer is not reported as a timeout" "$RUN_OUTPUT" "timed out"

SEQ="queued-somewhere-new"; run unknown-status
assert_true "an unrecognised status exits non-zero" test "$RUN_STATUS" -ne 0
assert_contains "the unknown status is named" "$RUN_OUTPUT" "unknown status"

# The correlation test: another deployment of the same compose service finished
# while ours is still running. Reporting that as success is the exact bug the
# title token exists to prevent.
SEQ="foreign-done"; TIMEOUT=2; run foreign
assert_true "somebody else's successful deployment is not ours" test "$RUN_STATUS" -ne 0
assert_contains "waiting continues until our own deployment resolves" "$RUN_OUTPUT" "timed out"

# ── Transport and HTTP errors ────────────────────────────────────────
DEPLOY_CODE=401; DEPLOY_BODY='{"code":"UNAUTHORIZED"}'; run unauthorized
assert_true "a rejected trigger exits non-zero" test "$RUN_STATUS" -ne 0
assert_contains "the HTTP status is reported" "$RUN_OUTPUT" "401"
refute_contains "a rejected trigger is never reported as success" "$RUN_OUTPUT" "finished successfully"

LIST_CODE=500; SEQ="running"; run list-error
assert_true "a failing status query exits non-zero" test "$RUN_STATUS" -ne 0
assert_contains "the status-query failure is named" "$RUN_OUTPUT" "deployment.allByCompose"

# ── Argument validation ──────────────────────────────────────────────
out=$(PATH="$STUB_BIN:$PATH" STUB_STATE="$WORK/state-args" DOKPLOY_URL="https://d.test" \
      DOKPLOY_COMPOSE_ID="cmp_123" "$SCRIPT" 2>&1); status=$?
assert_true "a missing API key exits non-zero" test "$status" -ne 0
assert_contains "the missing API key is named" "$out" "DOKPLOY_API_KEY"

# The token must never be acceptable on an argv: `ps` is world-readable, so the
# flag is refused outright rather than quietly ignored.
out=$(PATH="$STUB_BIN:$PATH" STUB_STATE="$WORK/state-args" DOKPLOY_URL="https://d.test" \
      DOKPLOY_COMPOSE_ID="cmp_123" "$SCRIPT" --api-key "$API_KEY" 2>&1); status=$?
assert_true "--api-key is rejected" test "$status" -ne 0
assert_contains "the rejection points at the environment variable" "$out" "DOKPLOY_API_KEY"
refute_contains "the rejected key is not echoed back" "$out" "$API_KEY"

out=$(PATH="$STUB_BIN:$PATH" STUB_STATE="$WORK/state-args" DOKPLOY_URL="https://d.test" \
      DOKPLOY_API_KEY="$API_KEY" "$SCRIPT" 2>&1); status=$?
assert_true "a missing compose id exits non-zero" test "$status" -ne 0
assert_contains "the missing compose id is named" "$out" "compose-id"

out=$("$SCRIPT" --help 2>&1); status=$?
assert_eq "--help exits 0" "0" "$status"
assert_contains "--help documents the endpoints" "$out" "compose.deploy"

out=$(PATH="$STUB_BIN:$PATH" STUB_STATE="$WORK/state-args" DOKPLOY_URL="https://d.test" \
      DOKPLOY_COMPOSE_ID="cmp_123" DOKPLOY_API_KEY="$API_KEY" "$SCRIPT" --nonsense 2>&1); status=$?
assert_true "an unknown flag exits non-zero" test "$status" -ne 0

# ── The script is a fail-closed shape, not just fail-closed today ────
script_text="$(cat "$SCRIPT")"
assert_match "the script aborts on unset variables and pipeline failures" "$script_text" '^set -euo pipefail'
assert_contains "the trigger endpoint is the documented one" "$script_text" "/api/compose.deploy"
assert_contains "the polling endpoint is the documented one" "$script_text" "/api/deployment.allByCompose"
assert_contains "authentication uses the documented header" "$script_text" "x-api-key"
# `ps` is world-readable on a shared runner, so the key goes in out of band.
assert_contains "the API key reaches curl through a config file" "$script_text" "--config -"
refute_match "no command-line flag carries the API key" "$script_text" '^[[:space:]]*--header .*x-api-key'

summary
