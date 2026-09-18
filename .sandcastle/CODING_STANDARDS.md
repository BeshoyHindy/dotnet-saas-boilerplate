# Coding Standards

The standards you are held to. This file is the index; the detail lives in `AGENTS.md`, the rule
files under `.agents/rules/`, and the decision records in `docs/adr/`. Read the rule file for an
area **before** you change it — every rule there exists because breaking it has already cost a
broken build or a silent bug.

## The shape of the codebase

A **modular monolith with vertical slices** on .NET 10, plus React client apps.

```
Host (composition root)  →  Modules.{Name} (runtime)  →  Modules.{Name}.Contracts (public API)
                         →  BuildingBlocks (shared framework)
```

- `src/BuildingBlocks/` — the shared framework (Core, Persistence, Web, Caching, Eventing, Storage,
  Quota, Jobs, Mailing, Shared).
- `src/Modules/{Name}/` — a bounded context: a runtime project (internal) plus a `.Contracts`
  project (commands, queries, DTOs, events, service interfaces).
- `src/Host/` — the API host, the migrator host, and the single migrations project.
- `src/Tests/` — unit projects per module, `Architecture.Tests`, and the container-backed
  integration projects.
- The React apps each own their `package.json`, lint, build and browser suite.

## Golden rules (do not break)

1. **Module boundaries.** A module references another module only through its `.Contracts` project.
   Cross-module work goes through Contracts service interfaces or integration events. Enforced by
   `Architecture.Tests` (NetArchTest).
2. **Do not modify `src/BuildingBlocks` without explicit human approval**
   (`.agents/rules/buildingblocks-protection.md`). If you believe a change there is required, make
   the change in the module instead, or stop and say so in your report.
3. **Tenant isolation is default-ON.** `BaseDbContext` applies the tenant query filter to every
   entity. Opt out only via `IGlobalEntity`. A subclass that overrides `OnModelCreating` **must call
   `base.OnModelCreating(modelBuilder)` LAST** or the filters are lost. Cross-tenant reads use
   `IgnoreQueryFilters()` **plus** an explicit re-filter — never rely on the filter's absence.
   Tenant is resolved from the token only (ADR-0002).
4. **Mediator handlers are `public sealed`**, implement `ICommandHandler<T,TResponse>` /
   `IQueryHandler<T,TResponse>`, return `ValueTask<T>`, and `.ConfigureAwait(false)` every await.
5. **Every command handler and every paginated query handler needs a `{Name}Validator`**
   (FluentValidation, same feature folder). Enforced by `HandlerValidatorPairingTests`.
6. **Propagate `CancellationToken`** into every Mediator send and every EF/IO call.
7. **Structured logging only** — message templates with named placeholders, or `[LoggerMessage]`
   source-gen on hot paths. Never string interpolation in a log message; the analyzers fail the
   build over it.
8. **Publish integration events through the outbox** (`IOutboxWriter.AddAsync`), never `IEventBus`
   directly, so the event commits with the business write.
9. **Frontend: pass per-call data through `mutate(arg)`**, and read it from the `variables` argument
   of `onMutate`/`onSuccess`/`onError` — never from component state the callbacks close over.
10. **Docs travel with the change.** A convention the next implementer could repeat belongs in the
    matching `.agents/rules/*.md` file, in the same commit.

## Area rules — read before you touch the area

| Area | File |
|---|---|
| Layering, module registration, middleware order, options | `.agents/rules/architecture.md` |
| Endpoints, CQRS, validation, ProblemDetails, permissions | `.agents/rules/api-conventions.md` |
| Entities, tenant filters, tracking, migrations | `.agents/rules/database.md` |
| Domain vs integration events, outbox/inbox, dispatch | `.agents/rules/eventing.md` |
| HybridCache keys, tags, invalidation | `.agents/rules/caching.md` |
| CORS, security headers, rate limiting, idempotency, quotas | `.agents/rules/security.md` |
| Serilog, correlation, OpenTelemetry | `.agents/rules/logging.md` |
| Storage, jobs, resilience | `.agents/rules/storage.md`, `jobs.md`, `resilience.md` |
| Unit tests | `.agents/rules/testing.md` |
| Container-backed tests | `.agents/rules/integration-testing.md` |
| A specific module's quirks | `.agents/rules/modules/{module}.md` |
| React apps | `.agents/rules/frontend/shared.md` + the app-specific file |

Architectural decisions that are settled — and the reasoning you should not re-litigate — are in
`docs/adr/`.

## Style

**Backend.** File-scoped namespaces · 4-space indent · explicit types (`var` only when the
right-hand side makes the type obvious) · `is null` / `is not null` · pattern matching and switch
expressions · `ArgumentNullException.ThrowIfNull` guards · records for DTOs, events and value
objects. The build runs with `TreatWarningsAsErrors`: a warning is a failure.

**Tests.** xUnit · Shouldly · NSubstitute · AutoFixture. Name methods
`MethodName_Should_ExpectedBehavior_When_Condition`, arrange-act-assert, and assert on observable
behaviour. When you assert a forwarded `CancellationToken`, assert the **specific** token —
NSubstitute fills optional parameters with `default`, so `Received(1).XAsync(arg)` silently asserts
`CancellationToken.None`.

**Frontend.** React 19 · Vite · TypeScript · TanStack Query v5 · React Router · Radix · Tailwind.
One fetch wrapper (`apiFetch`), hand-written DTO types (there is no codegen step), inline
hierarchical query keys, pages as named exports loaded lazily.

## Validation gates

Run the gate commands **you were given** for the areas your change touches, plus whatever the area
rules above name for them (a client app's lint and build; a scoped browser suite for a feature you
changed).

Run them **in the foreground** with an explicit `timeout` of up to 3600000 ms — the sandbox raises
the Bash cap to one hour. Do not background them and poll: suite output through a pipe is buffered
until exit. Ending your turn ends the run, the sandbox is torn down and any gate dies with it,
leaving the tree ungated — so never end your turn while a command is still running.

Report gate results as they actually came out. A skipped or failed gate stated plainly is useful; a
failed gate summarised as fine is a defect in the report.

### No Docker in the issue sandboxes

The container-backed test projects (`src/Tests/Integration.Tests`,
`src/Tests/Integration.Middleware.Tests`) need a Docker daemon, and the issue sandboxes have none.
They fail fast with `DockerUnavailableException`, which is environmental, not a regression — run the
unit projects to validate logic.

That makes a container-backed test you write here **unverified**. Nothing on an agent branch reaches
CI: Sandcastle never pushes it and never opens a pull request, so the host's post-merge gate is the
first thing that ever runs your test, and a wrong assertion there turns the merged HEAD red for the
whole round. So when you add or change one:

- **Pin the same behaviour first at a seam you can actually run** — a unit or handler test you ran.
  The container-backed test is the end-to-end echo of a fact already proven, never its only home.
- **Never guess an expected value.** Derive it from code you have read (the DTO's property names
  and `[JsonIgnore]`s, the options defaults, the exact clamp or skip arithmetic) and say in a comment
  where it comes from. Match JSON on the precise property (`"foo":`), not a bare substring a longer
  sibling name also matches.
- **Walk the guard clauses with your literal numbers** before trusting an assertion, to confirm the
  scenario actually reaches the branch you mean to assert on.
- **Set the Finbuckle tenant context in the SAME method that calls the code under test.** It is an
  `AsyncLocal`: a context set inside an awaited helper is gone by the time the caller resumes, the
  tenant filter then sees no tenant, and tenant-filtered queries quietly return nothing.
- **Remember the harness's eager wiring** — storage is re-registered after `AddHeroStorage` and rate
  limiting is read before host build. See `.agents/rules/integration-testing.md`.

### Browser suites are scoped, never full

Several sandboxes share one Docker VM. A full client suite takes tens of minutes contended, times
out in bulk, and then costs another hour of re-runs to separate flakes from regressions. Run the
specs `--only-changed` selects against your base commit, plus the spec folder(s) for the feature
areas your source change touched. CI runs every full suite on the eventual push, and that is the
regression net. Never run two clients' suites concurrently in one shell.

## Branching

Gitflow (ADR-0007). Agents work on their own `sandcastle/issue-<n>` branch and never commit to the
integration branch or to `main` directly — the orchestrator merges and gates for you.

**Never run `git worktree` in a sandbox** — not `add`, not `remove`, not `prune`. Your workspace IS
a host worktree bind-mounted into the container, sharing the host's `.git`. The host's other
worktree paths do not exist inside the container, so `git worktree prune` here deletes the host's
metadata for every sibling worktree and breaks every running agent's checkout, including yours. To
compare against an older commit use `git show <ref>:<path>`, `git diff <ref> -- <path>`, or
`git stash` on your own branch — never a second checkout.

## Security

Do not read, print, source, grep, or copy any `.env` file (root `.env`, `.sandcastle/.env`, any
`.env.local` or `.env.*`) — they hold real secrets. `.env.example` files are the safe reference. Do
not echo environment variables that look like tokens or keys, and never `git add` an `.env` file.
