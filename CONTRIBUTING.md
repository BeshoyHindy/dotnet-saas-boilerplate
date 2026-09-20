# Contributing

Thanks for helping out. The conventions below keep PRs reviewable.

## Reporting issues

- **Security:** Use GitHub's private advisories — see [SECURITY.md](SECURITY.md). Do not file public issues for vulnerabilities.
- **Bugs:** Open a GitHub issue on this repository with a minimal repro, your .NET SDK version, and the DB provider.
- **Features:** Open an issue describing the change before opening a PR for non-trivial work.

## Dev setup

Prerequisites: .NET 10 SDK, Docker, Node.js 20+.

```bash
dotnet build src/Boilerplate.slnx
<!--#if (aspire) -->
dotnet run --project src/Host/Boilerplate.AppHost   # full Aspire stack
<!--#endif -->
dotnet test src/Boilerplate.slnx                    # tests (integration suite needs Docker)
```

<!--#if (frontend) -->
The two clients live under `clients/dashboard` (tenant app, port 5173) and `clients/console` (operator tool, port 5174) — `pnpm install && pnpm dev` in either (ADR-0008).
<!--#endif -->

## Pull requests

- Branch from and target `develop` as `feature/<slug>`; `main` only receives `release/*` and `hotfix/*` merges (ADR-0007).
- Follow [Conventional Commits](https://www.conventionalcommits.org) — match the existing history (`feat(dashboard): ...`, `fix(identity): ...`).
- Add tests. The build runs with `TreatWarningsAsErrors=true`; analyzer warnings must be fixed.
- Don't touch `src/BuildingBlocks/` without prior discussion — wide blast radius.
- Architecture rules (module boundaries, file layout, coding style) are documented in [AGENTS.md](AGENTS.md), `.agents/rules/` and `docs/adr/`. Apply them. Name things the way [CONTEXT.md](CONTEXT.md) does.

## Licensing

Contributions are licensed under the terms this project ships under.
