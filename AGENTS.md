## Agent skills

### Issue tracker

Issues and specs live as GitHub issues on this repository, read and written with the `gh` CLI.

### Triage labels

Default vocabulary: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. Only `ready-for-agent` reaches an autonomous agent.

### Domain docs

Single-context: architecture decisions live in `docs/adr/`; the per-area conventions an agent must read before editing live in `.agents/rules/`.

### Branching

Gitflow (ADR-0007). Branch from `develop` as `feature/<slug>` (agents: `sandcastle/issue-<n>`) and open pull requests against `develop`. `main` receives only `release/*` and `hotfix/*` merges, each tagged `vX.Y.Z`. Never commit directly to `main` or `develop`.
