#!/usr/bin/env bash
# Contract tests for the two Dokploy compose stacks (ADR-0005).
#
# These assert the properties that make the stacks a deployment contract rather
# than a suggestion — pull-only images, a migrator that gates the API, the shared
# external network, a readiness probe Traefik actually performs, no secret in
# the repository, and an .env.example that is exactly the set of variables the
# files interpolate. Every one of them has a plausible "helpful" edit that would
# silently break a deploy, which is why they are tests and not a review note.
set -uo pipefail

TESTS_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOKPLOY_DIR="$(dirname "$TESTS_DIR")"

# shellcheck source=deploy/dokploy/tests/lib.sh
. "$TESTS_DIR/lib.sh"

DATA="$DOKPLOY_DIR/data-services.compose.yml"
APP="$DOKPLOY_DIR/app.compose.yml"
ENV_EXAMPLE="$DOKPLOY_DIR/.env.example"

# ── Helpers ──────────────────────────────────────────────────────────

# Print the YAML block of one service: from `  <name>:` up to the next line
# indented by exactly two spaces (the next service) or a top-level key.
service_block() {
  awk -v svc="$2" '
    $0 ~ "^  " svc ":[[:space:]]*$" { inside = 1; next }
    inside && /^  [^ ]/ { inside = 0 }
    inside && /^[^ ]/   { inside = 0 }
    inside             { print }
  ' "$1"
}

# Every ${VAR} / ${VAR:-...} referenced by a compose file, one per line, sorted.
compose_vars() {
  grep -ohE '\$\{[A-Za-z_][A-Za-z0-9_]*' "$@" | cut -c3- | sort -u
}

# Every key in .env.example, one per line, sorted.
env_keys() {
  grep -E '^[A-Za-z_][A-Za-z0-9_]*=' "$ENV_EXAMPLE" | cut -d= -f1 | sort -u
}

printf '%s\n' "── compose contract ──"

# ── Files exist ──────────────────────────────────────────────────────
assert_true "data-services.compose.yml exists" test -s "$DATA"
assert_true "app.compose.yml exists"           test -s "$APP"
assert_true ".env.example exists"              test -s "$ENV_EXAMPLE"

data_text="$(cat "$DATA")"
app_text="$(cat "$APP")"
both_text="${data_text}"$'\n'"${app_text}"

# ── Pull-only ────────────────────────────────────────────────────────
# A `build:` anywhere would make Dokploy compile on the production host, which
# is exactly the arrangement ADR-0005 rejects.
refute_match "data stack declares no build:" "$data_text" '^[[:space:]]*build:'
refute_match "app stack declares no build:"  "$app_text"  '^[[:space:]]*build:'
refute_match "no stack has a Dockerfile reference" "$both_text" '[Dd]ockerfile'

# ── App images all come from IMAGE_TAG ───────────────────────────────
app_images="$(printf '%s\n' "$app_text" | grep -E '^[[:space:]]*image:' | sed -E 's/^[[:space:]]*image:[[:space:]]*//')"
assert_eq "app stack has three images" "3" "$(printf '%s\n' "$app_images" | grep -c .)"
while IFS= read -r image; do
  [ -n "$image" ] || continue
  assert_match "app image is registry/owner/name:\${IMAGE_TAG} — $image" "$image" \
    '^\$\{IMAGE_REGISTRY\}/\$\{IMAGE_OWNER\}/[a-z0-9.-]+:\$\{IMAGE_TAG\}$'
done <<< "$app_images"
assert_contains "app pulls the published API image"      "$app_images" 'boilerplate-api:'
assert_contains "app pulls the published migrator image" "$app_images" 'boilerplate-db-migrator:'
assert_contains "app pulls the console image"            "$app_images" 'boilerplate-console:'
assert_match "app images are always re-pulled" "$app_text" '^[[:space:]]*pull_policy:[[:space:]]*always'

# ── Data-service images are pinned ───────────────────────────────────
data_images="$(printf '%s\n' "$data_text" | grep -E '^[[:space:]]*image:' | sed -E 's/^[[:space:]]*image:[[:space:]]*//')"
assert_true "data stack declares images" test -n "$data_images"
while IFS= read -r image; do
  [ -n "$image" ] || continue
  # An explicit tag, and never a floating one: the data plane must come back
  # byte-identical after a host reboot.
  assert_match "data image is pinned — $image" "$image" '^[a-z0-9./-]+:[A-Za-z0-9._-]+$'
  refute_match "data image is not :latest — $image" "$image" ':latest$'
done <<< "$data_images"
refute_match "data stack interpolates no image tag" "$data_images" '\$\{'

# ── Migrator gates the API ───────────────────────────────────────────
api_block="$(service_block "$APP" api)"
assert_match "api depends_on migrator" "$api_block" '^[[:space:]]*migrator:[[:space:]]*$'
assert_match "api waits for the migrator to complete successfully" "$api_block" \
  'condition:[[:space:]]*service_completed_successfully'
migrator_block="$(service_block "$APP" migrator)"
assert_match "migrator is one-shot (restart: \"no\")" "$migrator_block" '^[[:space:]]*restart:[[:space:]]*"no"'
assert_match "migrator applies and seeds" "$migrator_block" 'command:.*apply.*--seed'
# EnableAuthentication is off in the migrator, so a signing key there would be an
# unused secret in one more environment.
refute_match "migrator is given no JWT signing key" "$migrator_block" '[Jj]wt'
assert_match "migrator gets the seed admin password" "$migrator_block" 'Seed__DefaultAdminPassword'
assert_match "migrator gets the migrations assembly" "$migrator_block" 'DatabaseOptions__MigrationsAssembly'

# ── Object storage is open on `uploads/` and nowhere else ────────────
# Avatars and tenant branding are served as unsigned URLs under `uploads/`, so
# that prefix has to allow anonymous GET or every one of them 403s. Files-module
# objects live under `tenants/` where public and private share a key space and
# visibility is a database column, so widening this grant to the bucket would
# silently publish every private file and make ChangeFileVisibility a no-op.
init_block="$(service_block "$DATA" minio-init)"
public_block="$(service_block "$DATA" minio-public-prefix)"
assert_match "the bucket is created once, idempotently" "$init_block" 'command:.*mb.*--ignore-existing.*\$\{STORAGE_BUCKET\}'
assert_match "anonymous download is granted" "$public_block" 'command:.*anonymous.*set.*download'
assert_match "the grant is scoped to the uploads/ prefix" "$public_block" \
  'local/\$\{STORAGE_BUCKET\}/uploads'
refute_match "the grant is never the whole bucket" "$public_block" \
  '"local/\$\{STORAGE_BUCKET\}"\]'
refute_match "the grant never reaches the tenants/ prefix" "$data_text" 'download.*tenants'
assert_match "the grant runs after the bucket exists" "$public_block" \
  'condition:[[:space:]]*service_completed_successfully'
assert_match "the grant is one-shot" "$public_block" '^[[:space:]]*restart:[[:space:]]*"no"'

# ── The shared external network ──────────────────────────────────────
for f in "$DATA" "$APP"; do
  name="${f##*/}"
  net_block="$(awk '/^networks:/ { inside = 1; next } inside && /^[^ ]/ { inside = 0 } inside { print }' "$f")"
  assert_match "$name joins dokploy-network" "$net_block" '^[[:space:]]*dokploy-network:'
  assert_match "$name treats dokploy-network as external" "$net_block" '^[[:space:]]*external:[[:space:]]*true'
done

# ── Traefik: routing, readiness, host preservation ───────────────────
assert_match "api readiness is the Traefik load-balancer healthcheck on /health/ready" "$api_block" \
  'loadbalancer\.healthcheck\.path=/health/ready'
assert_match "api healthcheck sends the public Host" "$api_block" \
  'loadbalancer\.healthcheck\.hostname=\$\{API_DOMAIN\}'
assert_match "api healthcheck has an interval" "$api_block" 'loadbalancer\.healthcheck\.interval='
assert_match "api healthcheck has a timeout"  "$api_block" 'loadbalancer\.healthcheck\.timeout='
# X-Forwarded-Host is never honoured by the app, so the original Host has to
# survive the proxy hop or password-reset links point at the container IP.
assert_match "api keeps the original Host header" "$api_block" 'loadbalancer\.passhostheader=true'
assert_match "api is routed by Traefik" "$api_block" 'traefik\.enable=true'
assert_match "api is routed on the dokploy network" "$api_block" 'traefik\.docker\.network=dokploy-network'
assert_match "api terminates TLS with a cert resolver" "$api_block" 'tls\.certresolver=\$\{TRAEFIK_CERT_RESOLVER\}'
assert_match "api redirects plain HTTP to HTTPS" "$api_block" 'redirectscheme\.scheme=https'
assert_match "api load balancer targets the Kestrel port" "$api_block" 'loadbalancer\.server\.port=8080'

console_block="$(service_block "$APP" console)"
assert_match "console is routed by Traefik" "$console_block" 'traefik\.enable=true'
assert_match "console keeps the original Host header" "$console_block" 'loadbalancer\.passhostheader=true'
assert_match "console has a load-balancer healthcheck" "$console_block" 'loadbalancer\.healthcheck\.path='

# Traefik reports a middleware that two different containers define as a
# configuration error, so every redirect middleware name must be unique.
mw_names="$(printf '%s\n' "$both_text" | grep -oE 'traefik\.http\.middlewares\.[^.]+' | sort -u | wc -l | tr -d ' ')"
assert_eq "each routed service owns its redirect middleware" "3" "$mw_names"

# ── Production fail-fast contract ────────────────────────────────────
assert_match "api names the proxy network (ProxyOptions fails closed)" "$api_block" \
  'ProxyOptions__KnownNetworks__0:[[:space:]]*\$\{PROXY_KNOWN_NETWORK\}'
# An endpoint without the enable flag exports nothing: Production ships the OTLP
# exporter disabled, so shipping only the endpoint is silent dead configuration.
assert_match "api can actually turn the OTLP exporter on" "$api_block" \
  'OpenTelemetryOptions__Exporter__Otlp__Enabled:[[:space:]]*\$\{OTEL_EXPORTER_ENABLED\}'
assert_match "api is given an OTLP endpoint" "$api_block" \
  'OpenTelemetryOptions__Exporter__Otlp__Endpoint:[[:space:]]*\$\{OTEL_EXPORTER_OTLP_ENDPOINT\}'
refute_match "api never trusts any proxy" "$api_block" '^[[:space:]]*ProxyOptions__TrustAnyProxy:'
refute_match "api never enables X-Forwarded-Host" "$both_text" '[Xx]ForwardedHost'
assert_match "api sets an explicit AllowedHosts" "$api_block" '^[[:space:]]*AllowedHosts:[[:space:]]*\$\{ALLOWED_HOSTS\}'
refute_match "no stack sets AllowedHosts to *" "$both_text" 'AllowedHosts:[[:space:]]*.?\*'
assert_match "api runs as Production" "$api_block" 'ASPNETCORE_ENVIRONMENT:[[:space:]]*Production'
assert_match "migrator runs as Production" "$migrator_block" 'DOTNET_ENVIRONMENT:[[:space:]]*Production'
# The runtime image is chiseled: no shell, so a container HEALTHCHECK cannot run.
refute_match "api declares no container healthcheck" "$api_block" '^[[:space:]]*healthcheck:'

# ── Nothing is published on a host port ──────────────────────────────
# Everything reachable goes through Traefik; a `ports:` mapping would bypass it
# (and with it TLS, the host allow-list and the rate limiter).
refute_match "data stack publishes no host ports" "$data_text" '^[[:space:]]*ports:'
refute_match "app stack publishes no host ports"  "$app_text"  '^[[:space:]]*ports:'

# ── No literal secrets ───────────────────────────────────────────────
# Any assignment whose key looks credential-shaped must take its value from the
# environment, never from this repository.
secretish="$(printf '%s\n' "$both_text" \
  | grep -vE '^[[:space:]]*#' \
  | grep -iE '(password|secretkey|signingkey|apikey|accesskey|api_key|_token)[[:space:]]*[:=]' || true)"
assert_true "stacks assign at least one credential from the environment" test -n "$secretish"
offenders="$(printf '%s\n' "$secretish" | grep -vE '\$\{[A-Za-z_][A-Za-z0-9_]*\}' || true)"
assert_eq "every credential-shaped value is interpolated, none literal" "" "$offenders"

# ── .env.example is exactly the variables the stacks use ─────────────
used="$(compose_vars "$DATA" "$APP")"
declared="$(env_keys)"
undocumented="$(comm -23 <(printf '%s\n' "$used") <(printf '%s\n' "$declared") | tr '\n' ' ' | sed 's/ *$//')"
unused="$(comm -13 <(printf '%s\n' "$used") <(printf '%s\n' "$declared") | tr '\n' ' ' | sed 's/ *$//')"
assert_eq "every \${VAR} used by a stack is documented in .env.example" "" "$undocumented"
assert_eq "every key in .env.example is used by a stack" "" "$unused"

# .env.example is a contract, not a config: values would be either wrong or a
# leaked secret, and Dokploy reads none of them.
valued="$(grep -E '^[A-Za-z_][A-Za-z0-9_]*=.+' "$ENV_EXAMPLE" || true)"
assert_eq ".env.example carries key names only, no values" "" "$valued"

# ── docker compose accepts both files ────────────────────────────────
if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
  tmp_env="$(mktemp -t dokploy-contract-env.XXXXXX)"
  trap 'rm -f "$tmp_env"' EXIT
  while IFS= read -r key; do
    case "$key" in
      STACK_NAME)      value="teststack" ;;
      *_DOMAIN)        value="example.test" ;;
      SMTP_PORT)       value="587" ;;
      IMAGE_REGISTRY)  value="ghcr.io" ;;
      IMAGE_TAG)       value="0.0.0-test" ;;
      *)               value="unset-in-tests" ;;
    esac
    printf '%s=%s\n' "$key" "$value" >> "$tmp_env"
  done <<< "$declared"

  for f in "$DATA" "$APP"; do
    assert_true "docker compose config -q accepts ${f##*/}" \
      docker compose --env-file "$tmp_env" -f "$f" config -q
  done
else
  skip "docker compose config (docker is not available here)"
fi

summary
