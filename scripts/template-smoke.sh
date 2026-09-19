#!/usr/bin/env bash
# Template smoke (ADR-0001): prove that `dotnet new install . && dotnet new saas -n Acme`
# still yields a building, tested, fully renamed project — for the default parameter
# set and for each parameter turned off.
#
#   bash scripts/template-smoke.sh                       # defaults: everything on
#   bash scripts/template-smoke.sh --frontend false
#   bash scripts/template-smoke.sh --aspire false
#   bash scripts/template-smoke.sh --sandcastle false
#   bash scripts/template-smoke.sh --skip-node           # .NET only, for a fast loop
#   bash scripts/template-smoke.sh --keep                # leave the scaffold for inspection
#
# .github/workflows/template-smoke.yml calls exactly this script, so what CI proves
# is what you can reproduce locally.
#
# Isolation rules this script keeps:
#   * the scaffold is created in `mktemp -d`, outside the repository, so nothing it
#     produces can be picked up by the repo's own build, gates or git status;
#   * the template is installed into a dedicated `--debug:custom-hive`, so the
#     user's real `dotnet new` template cache is never touched;
#   * the hive is uninstalled and the temp tree deleted on exit, success or failure.
set -euo pipefail

FRONTEND=true
ASPIRE=true
SANDCASTLE=true
NAME=Acme
KEEP=false
SKIP_NODE=false

while [ $# -gt 0 ]; do
  case "$1" in
    --frontend)   FRONTEND="${2:?--frontend needs true|false}"; shift 2 ;;
    --aspire)     ASPIRE="${2:?--aspire needs true|false}"; shift 2 ;;
    --sandcastle) SANDCASTLE="${2:?--sandcastle needs true|false}"; shift 2 ;;
    --name)       NAME="${2:?--name needs a value}"; shift 2 ;;
    --keep)       KEEP=true; shift ;;
    --skip-node)  SKIP_NODE=true; shift ;;
    -h|--help)    sed -n '2,20p' "$0"; exit 0 ;;
    *)            echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd -P)"
# `pwd -P`, not the raw mktemp path: on macOS TMPDIR lives under /var, a symlink to
# /private/var, and MSBuild then sees each project under two paths — it restores and
# builds every project twice and, worse, resolves src/.editorconfig for only one of
# them, so analyzer severities silently revert and -warnaserror fails on rules the
# repo has turned off.
WORK="$(cd "$(mktemp -d "${TMPDIR:-/tmp}/template-smoke.XXXXXX")" && pwd -P)"
HIVE="$WORK/hive"
OUT="$WORK/$NAME"

cleanup() {
  local status=$?
  # Best-effort: an uninstall failure must not mask the real exit code.
  dotnet new uninstall "$REPO" --debug:custom-hive "$HIVE" >/dev/null 2>&1 || true
  if [ "$KEEP" = true ]; then
    echo "--keep: scaffold left at $OUT"
  else
    rm -rf "$WORK"
  fi
  exit "$status"
}
trap cleanup EXIT

step() { printf '\n\033[1m==> %s\033[0m\n' "$*"; }
fail() { echo "::error::$*"; echo "TEMPLATE SMOKE FAILED: $*" >&2; exit 1; }

echo "template-smoke: name=$NAME frontend=$FRONTEND aspire=$ASPIRE sandcastle=$SANDCASTLE"
echo "template-smoke: workspace $WORK"

# ── Scaffold ─────────────────────────────────────────────────────────
step "Installing the template from $REPO into a throwaway hive"
dotnet new install "$REPO" --debug:custom-hive "$HIVE"

step "Scaffolding $NAME"
# --skipRestore: the build below restores anyway, and the post-action's restore
# would double the .NET work in every variant.
dotnet new saas -n "$NAME" -o "$OUT" \
  --frontend "$FRONTEND" --aspire "$ASPIRE" --sandcastle "$SANDCASTLE" \
  --skipRestore true --debug:custom-hive "$HIVE"

# ── The rename gate ──────────────────────────────────────────────────
# Deliberately run BEFORE any restore, npm install or build: a pristine scaffold
# has no .git, no bin/obj, no node_modules and no lockfile caches, so this grep
# needs ZERO exclusions and any hit is a real hit. (Running it after a build would
# force exclusions for restore artefacts and dependency trees, which is exactly
# where a missed rename could then hide.)
step "grep -ri boilerplate over the scaffold (must be empty, no exclusions)"
if hits="$(grep -ri boilerplate "$OUT" || true)" && [ -n "$hits" ]; then
  echo "$hits" | head -50
  fail "the scaffold still contains the placeholder name"
fi
echo "No 'boilerplate' in $OUT."

step "Brand gate over the scaffold"
# scripts/brand-gate.sh answers about tracked files, so the scaffold needs a git
# index; it is a temp directory, and the repository under test is never touched.
git -C "$OUT" init -q
git -C "$OUT" add -A
(cd "$OUT" && bash "$REPO/scripts/brand-gate.sh")
rm -rf "$OUT/.git"

# ── Structural assertions ────────────────────────────────────────────
step "Structure"
exists()     { [ -e "$OUT/$1" ] || fail "expected $1 in the scaffold"; }
not_exists() { [ ! -e "$OUT/$1" ] || fail "$1 must not be in the scaffold"; }

exists "src/$NAME.slnx"
exists "src/Host/$NAME.Api"
exists "docker-compose.yml"
exists "scripts/local-env.sh"
exists ".github/workflows/backend.yml"
# The sandcastle pipeline has to be able to work a fresh scaffold, so the agent
# and contributor conventions travel with it.
exists "AGENTS.md"
exists "CLAUDE.md"
exists ".agents/rules/architecture.md"
exists "CONTRIBUTING.md"
exists "SECURITY.md"
# Repo-only files the product must not inherit.
not_exists ".git"
not_exists "LICENSE"
not_exists ".template.config"
not_exists ".agents/skills"
not_exists ".agents/workflows"
not_exists "skills-lock.json"
not_exists ".github/workflows/template-smoke.yml"
not_exists ".github/workflows/brand-gate.yml"
not_exists "docs/adr/0001-placeholder-namespace-and-dotnet-new-rename.md"
# Local state that must never be copied out of a working clone.
[ -z "$(find "$OUT" -name node_modules -o -name obj -o -name bin -o -name '.env' | head -1)" ] \
  || fail "the scaffold carries build output, node_modules or a .env"

if [ "$ASPIRE" = true ]; then
  exists "src/Host/$NAME.AppHost"
  grep -q "$NAME.AppHost" "$OUT/src/$NAME.slnx" || fail "the AppHost is missing from the solution"
else
  not_exists "src/Host/$NAME.AppHost"
  ! grep -q 'AppHost' "$OUT/src/$NAME.slnx" || fail "the solution still references the AppHost"
fi

if [ "$FRONTEND" = true ]; then
  exists "clients"
  exists ".github/workflows/frontend.yml"
  exists ".agents/rules/frontend"
  exists "scripts/export-openapi.sh"
  grep -q '^  console:' "$OUT/docker-compose.yml" || fail "docker-compose lost the console service"
else
  not_exists "clients"
  not_exists ".github/workflows/frontend.yml"
  not_exists ".agents/rules/frontend"
  not_exists "scripts/export-openapi.sh"
  ! grep -q '^  console:' "$OUT/docker-compose.yml" || fail "docker-compose still has a console service"
  ! grep -q '^  console:' "$OUT/deploy/dokploy/app.compose.yml" || fail "the deploy stack still has a console service"
  ! grep -q 'AddJavaScriptApp' "$OUT/src/Host/$NAME.AppHost/AppHost.cs" 2>/dev/null \
    || fail "the AppHost still starts a client app"
fi

if [ "$SANDCASTLE" = true ]; then
  exists ".sandcastle"
  exists "sandcastle.config.mts"
  exists "package.json"
else
  not_exists ".sandcastle"
  not_exists "sandcastle.config.mts"
  not_exists "package.json"
  not_exists "pnpm-lock.yaml"
  not_exists ".github/workflows/sandcastle.yml"
fi
echo "Structure OK."

# `dotnet new` copies file CONTENT, never the POSIX executable bit, so every .sh
# in a fresh scaffold is 644. Nothing in the repo depends on the bit (docs, CI and
# compose all invoke scripts as `bash <script>`) except deploy/dokploy/tests, which
# asserts it deliberately. The scaffolded README tells the user to run exactly this
# chmod once; do the same here so the deploy contract runs against a realistic tree.
find "$OUT" -name '*.sh' -exec chmod +x {} +

# ── .NET ─────────────────────────────────────────────────────────────
step "dotnet build (warnings as errors)"
dotnet build "$OUT/src/$NAME.slnx" -c Release -warnaserror

# Architecture + unit suites only. The Testcontainers integration suites are
# deliberately out: they need Docker and they prove the code, not the rename.
step "Architecture + unit tests"
for proj in Architecture Auditing Caching Generic Identity Multitenancy Files Framework; do
  echo "--- ${proj}.Tests"
  dotnet test "$OUT/src/Tests/${proj}.Tests" -c Release --no-build
done

# ── Deploy contract ──────────────────────────────────────────────────
# Pure bash assertions over the compose stacks and the env contract — no Docker,
# a few seconds, and the one gate that notices when a conditioned-out service
# leaves an orphan variable behind.
step "Deploy contract tests"
bash "$OUT/deploy/dokploy/tests/run.sh"

# ── Node ─────────────────────────────────────────────────────────────
if [ "$SKIP_NODE" = true ]; then
  echo "--skip-node: skipping the client build and the orchestrator tests."
  echo "TEMPLATE SMOKE PASSED (frontend=$FRONTEND aspire=$ASPIRE sandcastle=$SANDCASTLE, node skipped)"
  exit 0
fi

# Every client app is discovered by glob, and its package manager by lockfile, so
# nothing here names a client directory. Issue #14 replaces clients/admin +
# clients/dashboard with a single pnpm clients/console and this loop is unchanged.
if [ "$FRONTEND" = true ]; then
  found_client=false
  for pkg in "$OUT"/clients/*/package.json; do
    [ -f "$pkg" ] || continue
    app="$(dirname "$pkg")"
    found_client=true
    step "Building client $(basename "$app")"
    if [ -f "$app/pnpm-lock.yaml" ]; then
      (cd "$app" && pnpm install --frozen-lockfile && pnpm run build)
    else
      (cd "$app" && npm ci && npm run build)
    fi
  done
  [ "$found_client" = true ] || fail "--frontend true but no clients/*/package.json was scaffolded"
fi

if [ "$SANDCASTLE" = true ]; then
  step "Sandcastle orchestrator tests"
  (cd "$OUT" && pnpm install --frozen-lockfile && pnpm typecheck:sandcastle && pnpm test:sandcastle)
fi

echo
echo "TEMPLATE SMOKE PASSED (frontend=$FRONTEND aspire=$ASPIRE sandcastle=$SANDCASTLE)"
