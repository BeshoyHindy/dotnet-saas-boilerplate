# Architecture rules

Modular Monolith + Vertical Slice Architecture (VSA). Read this before adding/moving modules or touching wiring.

## Layers & dependency direction

```
Host (composition root)  →  Modules.{Name} (runtime)  →  Modules.{Name}.Contracts (public API)
                         →  BuildingBlocks (shared framework)
```

- **BuildingBlocks** (`src/BuildingBlocks/`) — Core, Persistence, Web, Caching, Eventing, Storage, Jobs, Mailing, Shared. Consumed by all modules. **Do not modify without explicit approval.**
- **Modules** (`src/Modules/{Name}/`) — bounded contexts. Each = a runtime project (internal) + a `.Contracts` project (public API: commands, queries, events, DTOs, service interfaces). The five the template ships (Identity, Multitenancy, Auditing, Files, Notifications) are **platform modules** (ADR-0003); a product adds **product modules**, one per bounded context of its own, not one per feature (ADR-0010).
- A module **MUST NOT** reference another module's runtime project — only its `.Contracts`. Enforced by `Architecture.Tests` (NetArchTest).
- A product module may reference a platform module's `.Contracts`; a platform module **never** references a product module, runtime or `.Contracts`. Enforced by `PlatformModuleDirectionTests`, which lists the five platform modules by name. It keeps the platform modules mergeable from the upstream template.

## Module = runtime + Contracts

```
Modules.Identity/            ← runtime (internal): handlers, services, domain, data
Modules.Identity.Contracts/  ← public: ICommand/IQuery types, DTOs, events, service interfaces
```

Cross-module communication: through Contracts service interfaces or integration events only.

## Feature folder layout (VSA)

Each feature is a vertical slice in `Features/v{version}/{Feature}/`. A module with several areas adds
one level, `Features/v{version}/{Area}/{Feature}/` — Identity does (`Users/`, `Roles/`, `Groups/` …);
the four smaller modules are flat, and a new small module should be too.

```
Features/v1/Users/RegisterUser/          # Identity: {Area}/{Feature}
├── RegisterUserEndpoint.cs          # minimal API endpoint
├── RegisterUserCommandHandler.cs    # CQRS handler (public sealed)
└── RegisterUserCommandValidator.cs  # FluentValidation
Features/v1/ListNotifications/           # Notifications: {Feature}
```

The Contracts project mirrors it: `Contracts/v1/[{Area}/]{Feature}/` holds that feature's command or
query and its response. Files and Notifications instead group Contracts by kind
(`Contracts/v1/Commands/`, `Queries/`, `DTOs/`); that predates the rule — don't copy it into a new
module.

Module support folders: `Domain/`, `Data/`, `Services/`, `Events/`, `Authorization/`.

## IModule registration

Each module implements `IModule`, declared via an **assembly-level** `[AppModule]` attribute (positional `(Type moduleType, int order = 0)`) — **not** a class-level `[AppModule(Order = n)]`:

```csharp
[assembly: AppModule(typeof(Boilerplate.Modules.Identity.IdentityModule), 100)]   // above the namespace

namespace Boilerplate.Modules.Identity;

public sealed class IdentityModule : IModule
{
    public void ConfigureServices(IHostApplicationBuilder builder) { ... }
    public void ConfigureMiddleware(IApplicationBuilder app) { ... }   // optional, runs AFTER UseAuthentication
    public void MapEndpoints(IEndpointRouteBuilder endpoints) { ... }
}
```

`ModuleLoader.AddModules` (`src/BuildingBlocks/Web/Modules/ModuleLoader.cs`) discovers `[AppModule]` attributes, orders by `Order` then name, instantiates each, and calls `ConfigureServices`. Endpoints map under `api/v{version:apiVersion}/{module}`.

Order: platform modules sit below 1000 (Identity 100 … Notifications 750). A product module takes **1000 and up, in steps of 100**.

## Registering a module: seven edits

A new module is seven edits. The four host lists are checked by `HostModuleListTests` (Architecture.Tests), which fails naming the list and the file when an `[AppModule]` assembly is missing from one:

| # | Edit | File | Caught by |
|---|---|---|---|
| 1–2 | The runtime and the `.Contracts` project | `src/Boilerplate.slnx` | nothing: the projects still build through the Migrations reference; only the solution view lacks them |
| 3 | `ProjectReference` to the runtime project, plus `<Folder Include="{Module}\" />` — how both hosts reach the module | `src/Host/Boilerplate.Migrations.PostgreSQL/Boilerplate.Migrations.PostgreSQL.csproj` | the build: the host lists name the module's types, which the hosts reach only through this reference |
| 4 | `HostModules.All` | `src/Host/Boilerplate.Api/HostModules.cs` | `HostModules_Should_List_Every_AppModule_Assembly` |
| 5 | Mediator `o.Assemblies`: a type from the **Contracts** assembly **and** one from the **runtime** assembly (the generator scans each type's assembly, so a command, DTO or `*ContractsMarker` all work) | `src/Host/Boilerplate.Api/Program.cs` | `MediatorAssemblies_Should_Register_Every_AppModule_Handlers` |
| 6 | `HostModules.All` | `src/Host/Boilerplate.DbMigrator/HostModules.cs` | `HostModules_Should_List_Every_AppModule_Assembly` |
| 7 | Mediator `o.Assemblies` (same pair) | `src/Host/Boilerplate.DbMigrator/Program.cs` | `MediatorAssemblies_Should_Register_Every_AppModule_Handlers` |

`o.Assemblies` stays a literal `typeof` list because Mediator's source generator reads it as written; the test calls each host's generated `AddMediator` and checks every module's handlers are registered. A product module also gets its own `.agents/rules/modules/<name>.md`. The full walk-through is `docs/new-project-guide.md` §3, *A new product module*.

## DI & handler conventions

- Mediator handlers: `public sealed`, implement `ICommandHandler<T,TResponse>` / `IQueryHandler<T,TResponse>`, return `ValueTask<T>`, `.ConfigureAwait(false)` on every await. `ServiceLifetime.Scoped`.
- Validators auto-register via `ModuleLoader` (`AddValidatorsFromAssemblies`). Name them `{Command}Validator`.
- Prefer constructor injection / primary constructors. Watch DI lifetimes: stateful singletons must be thread-safe (use `ConcurrentDictionary` / immutable snapshots).

## Middleware ordering (critical)

In `src/BuildingBlocks/Web/Extensions.cs` (`UseAppPlatform`):

0. **ForwardedHeaders first** (when `ProxyOptions.Enabled`) — everything downstream that reads the scheme or client IP must see the caller's values, not the proxy's
1. ExceptionHandler → ResponseCompression
2. **CORS before HTTPS redirect** (so OPTIONS preflight isn't 307-redirected)
3. HttpsRedirection → SecurityHeaders → static files → Routing
4. **`UseAuthentication`**
5. **`UseModuleMiddlewares`** — each module's `ConfigureMiddleware`, runs **after** auth
6. RateLimiting → `UseAuthorization` → `MapModules`

Finbuckle's `app.UseMultiTenant()` is installed by `MultitenancyModule.ConfigureMiddleware`, i.e. inside step 5 and therefore **after `UseRouting` and `UseAuthentication`** — tenant resolution is claim-driven (ADR-0002). The host must not call it. See `modules/multitenancy.md`.

## Static/global state

No global mutable static collections enumerated under concurrency. `Audit` (Auditing module) swaps an immutable `IAuditEnricher[]` atomically; `ModuleLoader` guards with a lock. Follow that pattern if you must hold process-global state.

## Configuration & options

- `appsettings.json` (+ `.Development`/`.Production`) live in `src/Host/Boilerplate.Api/`. DbMigrator links the same files.
- Bind config with the Options pattern: `AddOptions<T>().BindConfiguration(nameof(T))`, section name == type name (e.g. `JwtOptions`, `DatabaseOptions`, `CachingOptions`, `CorsOptions`, `RateLimitingOptions`; **storage section is `Storage`**, not `StorageOptions`). Add `.ValidateDataAnnotations().ValidateOnStart()` for fail-fast.
- Validate critical options via `IValidatableObject` — `JwtOptions` requires `SigningKey` ≥32 chars and **rejects placeholder strings containing `"replace-with"`**; `DatabaseOptions` rejects empty connection strings.
- Where a rule needs DI or the environment, data annotations can't express it: register an **`IValidateOptions<T>`** instead. Existing ones: `JwtOptionsProductionValidator` (Production placeholder/localhost keys), `CorsOptionsValidator` (`AllowAll` banned in Production, origins must be absolute and slash-free), `RateLimitingOptionsValidator` (per-policy ranges, only when `Enabled`), `ProxyOptionsValidator` (parseable proxy addresses, "enabled but trusting nobody").
- **Production fail-fast** (`Program.cs` → `builder.ValidateProductionConfiguration()`, before service registration): missing `DatabaseOptions:ConnectionString` / `CachingOptions:Redis` / `JwtOptions:SigningKey`, a secret that still matches `PlaceholderSecret.Looks(...)`, or an `AllowedHosts` that is empty or contains `*` all throw.
- **No credential ever lands in `appsettings*.json`.** Dev secrets via `dotnet user-secrets` — `bash scripts/dev-secrets.sh` bootstraps them (API and DbMigrator share one `UserSecretsId`); the AppHost passes Aspire parameters; deployments pass environment variables. `gitleaks dir .` (config in `.gitleaks.toml`) must stay clean.
- Platform composition is one call each: `builder.AddAppPlatform(o => { o.Enable... })` (DI) and `app.UseAppPlatform(...)` (middleware). Toggles cover Caching/Jobs/Mailing/OpenTelemetry/CORS/OpenAPI/Idempotency.
