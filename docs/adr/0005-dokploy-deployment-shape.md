---
status: accepted
---
# Dokploy: pull-only compose stacks, one-shot migrator, scripted deploy

Dokploy is the primary deployment target. CI builds and pushes images to GHCR; Dokploy never builds. Two compose stacks live in `deploy/dokploy/`: `data-services` (PostgreSQL, Valkey, S3-compatible storage, with healthchecks and named volumes) and `app` (`migrator`, `api` and the front end it serves). Separating them means an app redeploy can never recreate the database.

- **Migrations** run as a one-shot `migrator` service; `api` starts only on `service_completed_successfully`. The API never migrates or seeds at startup. The migrator takes a PostgreSQL advisory lock, so concurrent deploys are safe.
- **Routing** is Traefik labels on the shared external `dokploy-network`. The API image is chiseled (no shell), so container healthchecks are impossible; readiness is a Traefik load-balancer healthcheck on `/health/ready` plus a post-deploy probe of `/health/live` and `/health/ready` in CI.
- **Deploys** are triggered by `deploy/dokploy/dokploy-deploy.sh`, which calls the Dokploy API, correlates the deployment it started, polls it to completion and fails closed. A fire-and-forget webhook cannot fail a pipeline.
- **Secrets** are Dokploy environment variables, documented by key name in `deploy/dokploy/.env.example`; the API validates all options on start and refuses to boot in Production with placeholder values. A file-mounted secret bundle was rejected as infrastructure the template should not presume.

The upstream AWS Terraform stack is removed.
<!--#if (aspire) -->
Aspire remains the local development orchestrator;
<!--#endif -->
`docker compose` remains for running the production images locally.
