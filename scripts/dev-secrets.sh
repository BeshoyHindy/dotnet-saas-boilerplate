#!/usr/bin/env bash
# Bootstrap the local development secrets (issue #13). The repository ships no credentials, so a
# fresh clone needs this once — after that, `dotnet run --project src/Host/Boilerplate.AppHost`
# works as before. Values live in the .NET user-secrets store (outside the repo), never in
# appsettings.Development.json.
#
#   bash scripts/dev-secrets.sh                      # generate a signing key, keep existing values
#   SEED_ADMIN_PASSWORD='…' bash scripts/dev-secrets.sh
#   bash scripts/dev-secrets.sh --force              # regenerate even if already set
set -euo pipefail

cd "$(git rev-parse --show-toplevel)"

API_PROJECT="src/Host/Boilerplate.Api/Boilerplate.Api.csproj"
MIGRATOR_PROJECT="src/Host/Boilerplate.DbMigrator/Boilerplate.DbMigrator.csproj"

FORCE=0
[ "${1:-}" = "--force" ] && FORCE=1

# The API and the migrator share one UserSecretsId, so setting a value once covers both hosts;
# the migrator project is listed only so `dotnet user-secrets` initialises its store too.
set_secret() {
  local key="$1" value="$2"
  if [ "$FORCE" -eq 0 ] && dotnet user-secrets list --project "$API_PROJECT" 2>/dev/null | grep -q "^${key} = "; then
    echo "  = ${key} (already set, keeping)"
    return
  fi
  dotnet user-secrets set "$key" "$value" --project "$API_PROJECT" >/dev/null
  echo "  + ${key}"
}

echo "Development secrets -> user-secrets store of ${API_PROJECT}"

# 48 random bytes, base64; comfortably above the 32-character minimum HS256 signing key.
SIGNING_KEY="$(openssl rand -base64 48)"
set_secret "JwtOptions:SigningKey" "$SIGNING_KEY"

# Seeded root admin password (admin@root.com), used when the migrator runs standalone. Under Aspire
# the AppHost passes Seed__DefaultAdminPassword as an environment variable, which outranks
# user-secrets, so only set this when you explicitly choose one.
if [ -n "${SEED_ADMIN_PASSWORD:-}" ]; then
  set_secret "Seed:DefaultAdminPassword" "$SEED_ADMIN_PASSWORD"
else
  echo "  . Seed:DefaultAdminPassword (skipped — re-run with SEED_ADMIN_PASSWORD='…' to set one)"
fi

echo
echo "Done. Inspect with:"
echo "  dotnet user-secrets list --project $API_PROJECT"
echo "The migrator ($MIGRATOR_PROJECT) reads the same store."
