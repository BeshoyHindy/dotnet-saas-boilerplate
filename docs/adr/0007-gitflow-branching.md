---
status: accepted
---
# Gitflow: `develop` integrates, `main` is what production runs

`develop` is the default branch and the integration branch: feature work (`feature/*`) and agent work (`sandcastle/issue-*`) branch from it and merge back into it, and every merge deploys to staging. `main` only ever receives `release/*` and `hotfix/*` merges; each merge is tagged `vX.Y.Z`, builds the versioned images and deploys production. Hotfixes branch from `main` and merge into both.

Trunk-based development was the alternative and is the simpler default. We chose gitflow because the sandcastle pipeline merges unattended: agents need a branch they can land on continuously without that being a production release, and a human-cut `release/*` branch is the deliberate gate between "agents merged it" and "customers run it".

## Consequences

- The sandcastle integration branch is `develop` (ADR-0006); the post-merge gate runs there.
- CI: pull requests and pushes to `develop` run the full gates and deploy staging; tags on `main` publish versioned images and deploy production (ADR-0005).
- Nothing is committed directly to `main` or `develop` once branch protection is on.
