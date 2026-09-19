#!/usr/bin/env bash
# Minimal assertion helpers for the Dokploy deployment tests.
#
# Plain bash on purpose: these tests guard shell scripts and YAML, they have to
# run on a contributor's laptop and in CI without a toolchain, and the repository
# has no existing shell-test runner to join (.sandcastle/ is the agent
# orchestrator's node:test suite and ADR-0006 keeps it repo-agnostic). If a third
# suite ever wants bats, this is the file to replace.
#
# Source it, assert, then call `summary` as the last statement — it sets the
# exit status.

TESTS_PASSED=0
TESTS_FAILED=0

_pass() {
  TESTS_PASSED=$((TESTS_PASSED + 1))
  printf '  ok   %s\n' "$1"
}

_fail() {
  TESTS_FAILED=$((TESTS_FAILED + 1))
  printf '  FAIL %s\n' "$1"
  printf '       %s\n' "$2"
}

# assert_true <description> <command...>
assert_true() {
  local desc="$1"; shift
  if "$@" >/dev/null 2>&1; then _pass "$desc"; else _fail "$desc" "command failed: $*"; fi
}

# assert_false <description> <command...>
assert_false() {
  local desc="$1"; shift
  if "$@" >/dev/null 2>&1; then _fail "$desc" "command unexpectedly succeeded: $*"; else _pass "$desc"; fi
}

# assert_eq <description> <expected> <actual>
assert_eq() {
  if [ "$2" = "$3" ]; then _pass "$1"; else _fail "$1" "expected [$2], got [$3]"; fi
}

# assert_contains <description> <haystack> <needle>
assert_contains() {
  case "$2" in
    *"$3"*) _pass "$1" ;;
    *)      _fail "$1" "expected to find [$3] in: $(printf '%s' "$2" | head -c 400)" ;;
  esac
}

# refute_contains <description> <haystack> <needle>
refute_contains() {
  case "$2" in
    *"$3"*) _fail "$1" "did not expect to find [$3] in: $(printf '%s' "$2" | head -c 400)" ;;
    *)      _pass "$1" ;;
  esac
}

# assert_match <description> <text> <extended-regex>
assert_match() {
  if printf '%s\n' "$2" | grep -qE "$3"; then _pass "$1"; else _fail "$1" "no line matched /$3/"; fi
}

# refute_match <description> <text> <extended-regex>
refute_match() {
  if printf '%s\n' "$2" | grep -qE "$3"; then
    _fail "$1" "matched /$3/: $(printf '%s\n' "$2" | grep -E "$3" | head -3)"
  else
    _pass "$1"
  fi
}

skip() {
  printf '  skip %s\n' "$1"
}

summary() {
  printf '\n%s: %d passed, %d failed\n' "${0##*/}" "$TESTS_PASSED" "$TESTS_FAILED"
  [ "$TESTS_FAILED" -eq 0 ]
}
