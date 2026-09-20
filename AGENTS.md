## Agent skills

### Issue tracker

Issues and specs live as GitHub issues on this repository, read and written with the `gh` CLI. Conventions: [`docs/agents/issue-tracker.md`](docs/agents/issue-tracker.md).

### Triage labels

Default vocabulary: `needs-triage`, `needs-info`, `ready-for-agent`, `ready-for-human`, `wontfix`. Only `ready-for-agent` reaches an autonomous agent. Details: [`docs/agents/triage-labels.md`](docs/agents/triage-labels.md).

### Domain docs

Single-context: the glossary is [`CONTEXT.md`](CONTEXT.md), architecture decisions live in `docs/adr/`, and the per-area conventions an agent must read before editing live in `.agents/rules/`. How to consume them: [`docs/agents/domain.md`](docs/agents/domain.md).

Read `CONTEXT.md` before naming anything; it lists the words to avoid as well as the words to use.

### Branching

Gitflow (ADR-0007). Branch from `develop` as `feature/<slug>` (agents: `sandcastle/issue-<n>`) and open pull requests against `develop`. `main` receives only `release/*` and `hotfix/*` merges, each tagged `vX.Y.Z`. Never commit directly to `main` or `develop`.

## Conventions

Area rules live in `.agents/rules/`, one file per area — read the one covering what you are about to
change. `architecture.md` and `modules/*.md` carry the module rules; `buildingblocks-protection.md`
guards `src/BuildingBlocks/`, which is shared by every module and is not modified without explicit
approval. `frontend/clients.md` covers both React clients; `frontend/dashboard.md` and
`frontend/console.md` cover what is true of only one.

The tenancy invariant (ADR-0002) constrains almost everything: a caller never names a tenant, every
endpoint declares exactly one authorization intent, tenant-less background work is `[SystemJob]`, and
tenant-less integration events are `IGlobalIntegrationEvent`.
[`docs/new-project-guide.md`](docs/new-project-guide.md) §3 is the short version, with the test that
fails for each.

## Gates

```bash
dotnet build src/Boilerplate.slnx -warnaserror
dotnet test src/Boilerplate.slnx                    # integration suites need Docker
<!--#if (frontend) -->
cd clients/dashboard && pnpm test && pnpm build     # Vitest + type-checked build
cd clients/console   && pnpm test && pnpm build     # …and again for the operator tool
<!--#endif -->
bash deploy/dokploy/tests/run.sh                    # compose/env contract
<!--#if (sandcastle) -->
pnpm test:sandcastle                                # the agent pipeline's own suite
<!--#endif -->
gitleaks dir .                                      # must stay clean
```

<!--#if (frontend) -->
After an API-surface change, re-export the contract and regenerate BOTH clients' types
(`bash scripts/export-openapi.sh`, then `pnpm generate:api` in `clients/dashboard` and
`clients/console` — there are two clients, ADR-0008) and commit every artifact —
CI re-derives both sides and fails on drift. `bash scripts/check-openapi-drift.sh backend|frontend`
asks the same question locally.
<!--#endif -->
