## Agent skills

### Issue tracker

Issues and specs live as GitHub issues in `BeshoyHindy/dotnet-saas-boilerplate` (via the `gh` CLI). See `docs/agents/issue-tracker.md`.

### Triage labels

Default vocabulary: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. See `docs/agents/triage-labels.md`.

### Domain docs

Single-context: one `CONTEXT.md` and `docs/adr/` at the repo root. See `docs/agents/domain.md`.

### Branching

Gitflow (ADR-0007). Branch from `develop` as `feature/<slug>` (agents: `sandcastle/issue-<n>`) and open pull requests against `develop`. `main` receives only `release/*` and `hotfix/*` merges, each tagged `vX.Y.Z`. Never commit directly to `main` or `develop`.
