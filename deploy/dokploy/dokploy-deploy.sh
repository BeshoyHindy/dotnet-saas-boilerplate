#!/usr/bin/env bash
# Trigger a Dokploy Compose deployment and wait for THAT deployment to finish.
#
# Dokploy's deploy endpoint enqueues a job and answers immediately, so a plain
# webhook call can only ever report "the request was accepted" — a pipeline
# using one goes green while the deployment is still failing. This script fixes
# the deployment it started (by stamping a unique token into the deployment
# title) and polls until that exact row reaches a terminal status. It fails
# closed: a failed deployment, a timeout, an unparseable answer or a status it
# does not recognise all exit non-zero.
#
#   deploy/dokploy/dokploy-deploy.sh --compose-id <id>
#
# Endpoints (https://docs.dokploy.com/docs/api, "Compose" and "Deployment"):
#   POST /api/compose.deploy                       {"composeId","title","description"}
#   GET  /api/deployment.allByCompose?composeId=…  newest first
# Both authenticate with the `x-api-key` header. Deployment status is one of
# running | done | error | cancelled.
#
# Configuration (flags win over environment):
#   --url, DOKPLOY_URL                    https://dokploy.example.com (no /api)
#   --compose-id, DOKPLOY_COMPOSE_ID      Compose service id, from its URL
#   --api-key,  DOKPLOY_API_KEY           API token; prefer the environment
#   --timeout,  DOKPLOY_TIMEOUT_SECONDS   overall budget, default 900
#   --interval, DOKPLOY_POLL_SECONDS      poll period, default 10
#   --title,    DOKPLOY_DEPLOY_TITLE      human part of the deployment title
#
# The API key is never printed, and never passed on a command line to curl
# (`ps` is world-readable) — it goes in via a --config file on stdin.
set -euo pipefail

readonly PROGRAM="${0##*/}"

die() {
  printf '%s: %s\n' "$PROGRAM" "$*" >&2
  exit 1
}

log() {
  printf '[%s] %s\n' "$(date -u '+%Y-%m-%dT%H:%M:%SZ')" "$*"
}

usage() {
  sed -n '2,/^set -euo/p' "$0" | sed -e 's/^# \{0,1\}//' -e '$d'
}

# ── Arguments ────────────────────────────────────────────────────────
DOKPLOY_URL="${DOKPLOY_URL:-}"
DOKPLOY_COMPOSE_ID="${DOKPLOY_COMPOSE_ID:-}"
DOKPLOY_API_KEY="${DOKPLOY_API_KEY:-}"
timeout_seconds="${DOKPLOY_TIMEOUT_SECONDS:-900}"
poll_seconds="${DOKPLOY_POLL_SECONDS:-10}"
deploy_title="${DOKPLOY_DEPLOY_TITLE:-Scripted deploy}"

while [ "$#" -gt 0 ]; do
  case "$1" in
    --url)        DOKPLOY_URL="${2:-}"; shift 2 ;;
    --compose-id) DOKPLOY_COMPOSE_ID="${2:-}"; shift 2 ;;
    --api-key)    DOKPLOY_API_KEY="${2:-}"; shift 2 ;;
    --timeout)    timeout_seconds="${2:-}"; shift 2 ;;
    --interval)   poll_seconds="${2:-}"; shift 2 ;;
    --title)      deploy_title="${2:-}"; shift 2 ;;
    -h|--help)    usage; exit 0 ;;
    *)            die "unknown argument '$1' (try --help)" ;;
  esac
done

[ -n "$DOKPLOY_URL" ]        || die "missing --url / DOKPLOY_URL"
[ -n "$DOKPLOY_COMPOSE_ID" ] || die "missing --compose-id / DOKPLOY_COMPOSE_ID"
[ -n "$DOKPLOY_API_KEY" ]    || die "missing --api-key / DOKPLOY_API_KEY"
command -v curl >/dev/null 2>&1 || die "curl is required"
# jq rather than hand-rolled parsing: this script's whole value is deciding
# correctly whether a deployment succeeded, and a regex over JSON that is wrong
# on an escaped quote would decide it wrongly rather than loudly.
command -v jq >/dev/null 2>&1 || die "jq is required"

case "$timeout_seconds" in (*[!0-9]*|'') die "--timeout must be a whole number of seconds" ;; esac
case "$poll_seconds" in (*[!0-9]*|''|0) die "--interval must be a positive whole number of seconds" ;; esac

# Trailing slashes would produce //api/… , which Dokploy 404s.
base_url="${DOKPLOY_URL%/}"

# ── HTTP ─────────────────────────────────────────────────────────────
# Body and status code come back together: the body is everything up to the
# last line, the status code is the last line.
http_status=''
http_body=''

# call <method> <url> [json-body]
call() {
  local method="$1" url="$2" body="${3:-}"
  local response

  # The key reaches curl through a config file on stdin, so it is absent from
  # the process table and from any `set -x` trace of the arguments.
  if [ "$method" = POST ]; then
    response=$(printf 'header = "x-api-key: %s"\n' "$DOKPLOY_API_KEY" | curl \
      --silent --show-error --config - \
      --max-time 30 \
      --request POST \
      --header 'Content-Type: application/json' \
      --header 'Accept: application/json' \
      --data "$body" \
      --write-out '\n%{http_code}' \
      "$url") || return 1
  else
    response=$(printf 'header = "x-api-key: %s"\n' "$DOKPLOY_API_KEY" | curl \
      --silent --show-error --config - \
      --max-time 30 \
      --header 'Accept: application/json' \
      --write-out '\n%{http_code}' \
      "$url") || return 1
  fi

  http_status="${response##*$'\n'}"
  http_body="${response%$'\n'*}"
  return 0
}

# ── Trigger ──────────────────────────────────────────────────────────
# The correlation token. Dokploy stores the request's `title` verbatim on the
# deployment row it creates, and returns only `true` from the deploy call, so a
# token in the title is the only handle on "the deployment I just started".
# Concurrent deploys of the same compose service therefore stay distinguishable.
token="dpl-$(date -u '+%Y%m%d%H%M%S')-$$-${RANDOM}${RANDOM}"
title="${deploy_title} [${token}]"

log "Triggering deployment of compose ${DOKPLOY_COMPOSE_ID} (token ${token})"

payload=$(jq -nc --arg id "$DOKPLOY_COMPOSE_ID" --arg title "$title" \
  '{composeId: $id, title: $title, description: "Triggered by dokploy-deploy.sh"}')

call POST "${base_url}/api/compose.deploy" "$payload" \
  || die "could not reach ${base_url}/api/compose.deploy"

case "$http_status" in
  2??) : ;;
  *)   die "compose.deploy returned HTTP ${http_status:-<none>}: ${http_body}" ;;
esac

# ── Poll ─────────────────────────────────────────────────────────────
deadline=$(( $(date -u +%s) + timeout_seconds ))
status_url="${base_url}/api/deployment.allByCompose?composeId=${DOKPLOY_COMPOSE_ID}"
last_reported=''

while :; do
  sleep "$poll_seconds"

  if ! call GET "$status_url"; then
    log "status query failed (transport); retrying"
    [ "$(date -u +%s)" -lt "$deadline" ] || die "timed out after ${timeout_seconds}s waiting for deployment ${token}"
    continue
  fi

  case "$http_status" in
    2??) : ;;
    *)   die "deployment.allByCompose returned HTTP ${http_status:-<none>}: ${http_body}" ;;
  esac

  # `// empty` keeps a non-array or an array without our token from looking like
  # a status; a body that is not JSON at all fails jq, which is a hard failure —
  # an unreadable answer is not evidence of a healthy deployment.
  if ! status=$(printf '%s' "$http_body" \
      | jq -r --arg token "$token" 'map(select(.title? // "" | contains($token))) | first | .status // empty' 2>/dev/null); then
    die "could not parse the deployment list returned by Dokploy"
  fi

  case "$status" in
    '')
      # The queue has not created the row yet. Only time ends this branch.
      [ "$last_reported" = pending ] || log "waiting for Dokploy to register the deployment"
      last_reported=pending
      ;;
    running)
      [ "$last_reported" = running ] || log "deployment running"
      last_reported=running
      ;;
    done)
      log "deployment ${token} finished successfully"
      exit 0
      ;;
    error|cancelled)
      message=$(printf '%s' "$http_body" \
        | jq -r --arg token "$token" 'map(select(.title? // "" | contains($token))) | first | .errorMessage // ""' 2>/dev/null || true)
      die "deployment ${token} ended with status '${status}'${message:+: $message}"
      ;;
    *)
      # Fail closed. A status this script has never heard of is a Dokploy it does
      # not understand, and guessing "probably fine" is how a broken release ships.
      die "deployment ${token} reported unknown status '${status}'"
      ;;
  esac

  [ "$(date -u +%s)" -lt "$deadline" ] \
    || die "timed out after ${timeout_seconds}s waiting for deployment ${token} (last status: ${last_reported:-none})"
done
