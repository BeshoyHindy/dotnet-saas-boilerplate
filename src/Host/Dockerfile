# syntax=docker/dockerfile:1.7

# One image definition for both .NET host processes. A single `build` stage
# restores and publishes each project, then two thin runtime targets copy the
# matching publish output. The build context is the repository ROOT, because
# global.json + src/Directory.Build.props + src/Directory.Packages.props
# centralize versions for every project.
#
#   docker build -f src/Host/Dockerfile --target api      -t boilerplate/api:local .
#   docker build -f src/Host/Dockerfile --target migrator -t boilerplate/migrator:local .

ARG DOTNET_VERSION=10.0

# ── Shared build stage ──────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS build
WORKDIR /src

# global.json pins the SDK feature band (rollForward: latestFeature).
COPY global.json ./
COPY src/ ./src/

# Project-level restore, not `dotnet restore src/Boilerplate.slnx`: .dockerignore
# drops src/Tests/ from the context, so the solution would reference projects that
# aren't there. The two entrypoints pull the same transitive graph between them.
RUN dotnet restore src/Host/Boilerplate.Api/Boilerplate.Api.csproj \
 && dotnet restore src/Host/Boilerplate.DbMigrator/Boilerplate.DbMigrator.csproj

RUN dotnet publish src/Host/Boilerplate.Api/Boilerplate.Api.csproj \
    -c Release --no-restore -o /publish/api

RUN dotnet publish src/Host/Boilerplate.DbMigrator/Boilerplate.DbMigrator.csproj \
    -c Release --no-restore -o /publish/migrator

# ── Shared runtime base ─────────────────────────────────────────────
# `chiseled-extra` = chiseled (no shell, no package manager, no debugger — small
# pull, small CVE surface) PLUS the ICU and tzdata the app needs: the projects do
# not set InvariantGlobalization, so plain `chiseled` would fail at startup.
# Because there is no shell, container HEALTHCHECKs are impossible — readiness is
# probed from outside via /health/ready (ADR-0005).
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION}-noble-chiseled-extra AS runtime
WORKDIR /app
# APP_UID (1654) is the non-root user baked into the base image.
USER $APP_UID

# ── api ─────────────────────────────────────────────────────────────
FROM runtime AS api
COPY --from=build /publish/api .
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_ENVIRONMENT=Production
EXPOSE 8080
ENTRYPOINT ["dotnet", "Boilerplate.Api.dll"]

# ── migrator ────────────────────────────────────────────────────────
# One-shot: applies pending migrations under a Postgres advisory lock, then exits
# 0/1. The API never migrates at startup; it waits for this container to complete.
FROM runtime AS migrator
COPY --from=build /publish/migrator .
ENV DOTNET_ENVIRONMENT=Production
ENTRYPOINT ["dotnet", "Boilerplate.DbMigrator.dll"]
# Default verb; compose restates it explicitly so it is grep-able there.
CMD ["apply"]
