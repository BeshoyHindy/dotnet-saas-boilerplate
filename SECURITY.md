# Security Policy

## Supported versions

Only the latest release — the current `main` branch — receives security fixes. Older tags are not patched; upgrade to the latest release.

## Reporting a vulnerability

**Do not open a public issue.** Use GitHub's private vulnerability reporting on this repository
(Security → Report a vulnerability).

Please include:

- Affected component (module, file, endpoint)
- Reproduction steps and any required configuration
- Impact (what an attacker can achieve)
- Proof-of-concept if you have one

## What to expect

- Acknowledgement within 72 hours.
- Triage decision within 7 days.
- Coordinated disclosure window of ~90 days from triage, longer for changes that need careful migration paths.

Fixes ship as a patched commit on `main` plus a GitHub Security Advisory. Reporters are credited with permission.

## Scope

In scope: `src/` (BuildingBlocks, Modules, Host), the default `appsettings.*.json`, and anything this repository ships under `clients/`.

Out of scope: vulnerabilities in third-party NuGet/npm packages themselves (report them upstream; do tell us if one is exploitable through this project).

## Production hardening

Local development runs with development-friendly defaults. Before any deployment, rotate JWT signing keys and seeded passwords, lock CORS, keep the `Permissions.Hangfire.View` operator permission on the root tenant's Admin role only, and persist DataProtection keys to a shared store for multi-instance hosting.
