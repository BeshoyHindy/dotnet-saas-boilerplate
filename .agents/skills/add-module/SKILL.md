---
name: add-module
description: Create a new module (bounded context) — runtime + Contracts projects, IModule, DbContext, permissions, migrations, and the four registration lists. Use when adding a distinct business domain (a product module — ADR-0010). For a feature in an existing module, use add-feature.
argument-hint: "[ModuleName]"
---

# Add Module

High-ceremony. The part people get wrong is **registration — a module must be wired in FOUR lists**
(see Step 6). This recipe builds a **product module** (ADR-0010): the template's own five **platform
modules** (Identity, Multitenancy, Auditing, Files, Notifications — ADR-0003) already exist, and a
platform module never references a product module. Architecture rules: `.agents/rules/architecture.md`.

## Projects

```
src/Modules/{Name}/
├── Modules.{Name}/            ← runtime (internal): Domain/, Data/, Features/v1/, {Name}Module.cs
└── Modules.{Name}.Contracts/  ← public API: v1/ (commands/queries), Dtos/, Authorization/, Events/
```

**Copy an existing module's two `.csproj` files** (e.g. `Modules.Files`) and rename — don't hand-write
project references. The runtime project references its Contracts project + the BuildingBlocks it needs;
the Contracts project references `Mediator` + shared contracts.

## Step 1 — `[AppModule]` is an ASSEMBLY attribute (not class-level)

In `{Name}Module.cs`, above the namespace:

```csharp
[assembly: AppModule(typeof(Boilerplate.Modules.{Name}.{Name}Module), 1000)]   // (Type, order)

namespace Boilerplate.Modules.{Name};

public sealed class {Name}Module : IModule
{
    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddPermissions({Name}Permissions.All);
        builder.Services.AddAppDbContext<{Name}DbContext>();
        builder.Services.AddScoped<IDbInitializer, {Name}DbInitializer>();

        // Only if the module HANDLES integration events:
        // builder.Services.AddIntegrationEventHandlers(typeof({Name}Module).Assembly);
        //
        // Publishing needs no registration at all — the outbox is framework-owned
        // (host calls AddEventingCore once). Inject IOutboxWriter and publish.
        // Never register a per-module outbox store; see .agents/rules/eventing.md.

        builder.Services.AddHealthChecks()
            .AddDbContextCheck<{Name}DbContext>(name: "db:{name}");
    }

    public void ConfigureMiddleware(IApplicationBuilder app) { }   // optional, runs after auth

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var versionSet = endpoints.NewApiVersionSet().HasApiVersion(new ApiVersion(1)).ReportApiVersions().Build();
        var group = endpoints.MapGroup("api/v{version:apiVersion}/{name}")
            .WithTags("{Name}").WithApiVersionSet(versionSet).RequireAuthorization();
        // group.MapCreate{Entity}Endpoint();  …
    }
}
```

`Order` controls load sequence (platform modules: Auditing 300, Files 350, Notifications 750). Product modules take order 1000 and up, in steps of 100, so they start after every platform module. If your module consumes another's events, load after it.

## Step 2 — Permissions (Contracts/Authorization)

`{Name}Permissions` with nested resource classes and an `All` collection contributed via `services.AddPermissions({Name}Permissions.All)`. Mirror the shape of `FilesPermissions`.

## Step 3 — DbContext (extends `BaseDbContext`)

```csharp
public sealed class {Name}DbContext : BaseDbContext
{
    public const string Schema = "{name}";

    public {Name}DbContext(
        IMultiTenantContextAccessor<AppTenantInfo> multiTenantContextAccessor,
        DbContextOptions<{Name}DbContext> options,
        IOptions<DatabaseOptions> settings,
        IHostEnvironment environment)
        : base(multiTenantContextAccessor, options, settings, environment) { }

    public DbSet<{Entity}> {Entities} => Set<{Entity}>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof({Name}DbContext).Assembly);
        base.OnModelCreating(modelBuilder);   // MUST be last — applies tenant + soft-delete filters
    }
}
```

Your context is now covered by two architecture tests without any wiring: `TenantIsolationTests`
builds its model and fails on an entity that is neither tenant-filtered nor `IGlobalEntity`, and
`IgnoreQueryFiltersAllowListTests` fails on any `IgnoreQueryFilters` call in a file that is not
allow-listed. Both live in `Architecture.Tests`. See `.agents/rules/database.md`.

Likewise, every route you map in `MapEndpoints` joins the cross-tenant sweep in `Integration.Tests`
the moment it exists — a route with a resource id must answer **404** (never 403) for another
tenant's id, and needs either a `TenantSweepRegistry` entry it can seed or an explicit
`.ExemptFromTenantSweep("reason")`. See `.agents/rules/integration-testing.md`.

## Step 4 — Solution + project references

```bash
dotnet sln src/Boilerplate.slnx add src/Modules/{Name}/Modules.{Name}/Modules.{Name}.csproj
dotnet sln src/Boilerplate.slnx add src/Modules/{Name}/Modules.{Name}.Contracts/Modules.{Name}.Contracts.csproj
```

Add a `<ProjectReference>` to the runtime module from **both** `Boilerplate.Api` and `Boilerplate.DbMigrator`, and from `Boilerplate.Migrations.PostgreSQL` — the Migrations project's reference is how both hosts reach the module's migrations.

## Step 5 — Migrations folder

Add a `{Name}/` folder in `src/Host/Boilerplate.Migrations.PostgreSQL`, then create the initial migration (see **create-migration**) with `--context {Name}DbContext`.

## Step 6 — ⚠️ Register in ALL FOUR lists (the footgun)

Per host (**both** `Boilerplate.Api` and `Boilerplate.DbMigrator`), two edits:

1. `HostModules.cs` — add `typeof({Name}Module).Assembly` to `HostModules.All`.
2. `Program.cs` — Mediator `o.Assemblies` — add **two** markers: a Contracts type (e.g. `typeof(Boilerplate.Modules.{Name}.Contracts.{Name}ContractsMarker)`) **and** the module type (`typeof({Name}Module)`). This list stays a literal `typeof` list because Mediator's source generator reads it as written.

Miss the Mediator marker → handlers silently undiscovered. Miss the `HostModules.All` entry → module never loads. Miss the DbMigrator pair → migrate/seed skips the module. `HostModuleListTests` (Architecture.Tests) fails loudly and names the list and file to edit if you miss one.

## Step 7 — Verify

```bash
dotnet build src/Boilerplate.slnx                  # 0 warnings
dotnet test src/Tests/Architecture.Tests           # boundary + tenant-isolation rules must pass
dotnet test src/Boilerplate.slnx
```

## Checklist

- [ ] Two projects (copied csproj), added to `.slnx`, referenced from Api + DbMigrator (+ Migrations)
- [ ] `[assembly: AppModule(typeof({Name}Module), order)]` (assembly-level, positional)
- [ ] `IModule`: `AddAppDbContext<T>()`, `services.AddPermissions(...)`, version-set group, eventing trio if needed
- [ ] `{Name}DbContext : BaseDbContext`, 4-arg ctor, `base.OnModelCreating` last
- [ ] `{Name}Permissions` in Contracts/Authorization
- [ ] Migrations folder + initial migration (`--context {Name}DbContext`)
- [ ] **Registered in all four lists** (Api + DbMigrator × `HostModules.All` + Mediator `o.Assemblies`)
- [ ] Own rule file at `.agents/rules/modules/<name>.md`
- [ ] Build + Architecture.Tests green
