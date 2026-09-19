---
name: add-feature
description: Add a vertical-slice feature (command/query + handler + validator + endpoint) to an existing Boilerplate module. Use when adding an API endpoint or business operation to a module that already exists.
argument-hint: "[ModuleName] [Area] [FeatureName]"
---

# Add Feature

A feature is a vertical slice **split across two projects**: the request/response types live in the
module's `.Contracts` project (public API); the handler, validator, and endpoint live in the runtime
project. Full conventions: `.agents/rules/api-conventions.md`.

## Layout (real)

```
src/Modules/{X}/Modules.{X}.Contracts/v1/{Area}/{Feature}Command.cs   # ICommand<T>/IQuery<T>
src/Modules/{X}/Modules.{X}.Contracts/Dtos/{Entity}Dto.cs             # response DTOs (if any)
src/Modules/{X}/Modules.{X}/Features/v1/{Area}/{Feature}/
├── {Feature}CommandHandler.cs    # public sealed, injects the DbContext directly
├── {Feature}CommandValidator.cs  # required for commands + paginated queries
└── {Feature}Endpoint.cs          # internal static extension
```

## Step 1 — Command/Query (Contracts project)

`Mediator` interfaces (`using Mediator;`). Records. A create command can return the raw `Guid`.

```csharp
namespace Boilerplate.Modules.{X}.Contracts.v1.{Area};

public sealed record Create{Entity}Command(string Name, decimal PriceAmount, string PriceCurrency)
    : ICommand<Guid>;
```

Read/list DTOs go in `Modules.{X}.Contracts/Dtos/`. Paginated queries return `PagedResponse<T>`
(`Boilerplate.BuildingBlocks.Shared.Persistence`) — see `query-patterns`.

## Step 2 — Handler (runtime `Features/`) — inject the DbContext, NOT a repository

There is **no generic `IRepository<T>`**. Inject the module's `{X}DbContext`. `public sealed`, primary
ctor, `ValueTask<T>`, `.ConfigureAwait(false)`, guard first. Tenant/audit fields are auto-stamped — only
inject `ICurrentUser` if you need the acting user (`GetUserId()` / `GetTenant()`).

```csharp
public sealed class Create{Entity}CommandHandler({X}DbContext dbContext)
    : ICommandHandler<Create{Entity}Command, Guid>
{
    public async ValueTask<Guid> Handle(Create{Entity}Command command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var entity = {Entity}.Create(command.Name, new Money(command.PriceAmount, command.PriceCurrency));
        dbContext.{Entities}.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return entity.Id;
    }
}
```

Throw `NotFoundException` / `CustomException(msg, errors, HttpStatusCode)` (`Boilerplate.BuildingBlocks.Core.Exceptions`) — the global handler maps them to ProblemDetails.

## Step 3 — Validator (required; same folder)

```csharp
public sealed class Create{Entity}CommandValidator : AbstractValidator<Create{Entity}Command>
{
    public Create{Entity}CommandValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.PriceCurrency).NotEmpty().Length(3);
    }
}
```

`Architecture.Tests` fails the build if a command/paginated-query handler has no `{Name}Validator`.

## Step 4 — Endpoint (same folder)

```csharp
public static class Create{Entity}Endpoint
{
    internal static RouteHandlerBuilder MapCreate{Entity}Endpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/{entities}",
                async (Create{Entity}Command command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("Create{Entity}")
            .WithSummary("Create a {entity}")
            .RequirePermission({X}Permissions.{Entities}.Create)
            .WithIdempotency();   // on replay-safe POSTs
}
```

## Step 5 — Wire it in `{X}Module.MapEndpoints`

```csharp
group.MapCreate{Entity}Endpoint();   // group = endpoints.MapGroup("api/v{version:apiVersion}/{x}") …
```

## Step 5b — If the route takes an id, the cross-tenant sweep will find it

`Integration.Tests` enumerates every published route and calls each one that has a resource parameter
with another tenant's id, demanding **404**. You register nothing; it is swept because it exists.
What it may need from you:

- **A resource it can seed.** The registry key is `"{preceding literal segment}/{parameter name}"` —
  `/{entities}/{id}` → `entities/id`. A route under an existing noun already resolves; a new noun
  fails the coverage test with the exact key to add to `TenantSweepRegistry.ByRouteKey` (plus a
  `ResourceKind` and a seeder).
- **A body sample**, if your validator would 400 before the handler looks the row up —
  `TenantSweepBodies.ByEndpoint`, keyed `"METHOD template"`.

Answering **403** for another tenant's id fails the sweep too, and correctly: it confirms the row
exists and makes isolation depend on a permission. Throw `NotFoundException` and let the tenant query
filter do the work.

Genuinely not a tenant-scoped resource? Say so at the mapping site — the reason is mandatory and the
sweep prints it:

```csharp
.ExemptFromTenantSweep("{documentName} selects an API version document, not a tenant resource")
```

See `.agents/rules/integration-testing.md`.

## Step 6 — Verify

```bash
dotnet build src/Boilerplate.slnx          # 0 warnings (TreatWarningsAsErrors)
dotnet test src/Tests/{X}.Tests            # + add a handler/validator test (see testing-guide)
```

## Checklist

- [ ] Command/Query in the **Contracts** project (`using Mediator;`), DTOs in `Contracts/Dtos/`
- [ ] Handler `public sealed`, injects `{X}DbContext` (no repository), `ValueTask<T>` + `.ConfigureAwait(false)`
- [ ] `{Name}Validator` exists
- [ ] Endpoint `internal static …Map{Feature}Endpoint`, `.RequirePermission(...)`, `.WithName/.WithSummary`
- [ ] Wired in `{X}Module.MapEndpoints`
- [ ] Route with an id: resolves in `TenantSweepRegistry`, or `.ExemptFromTenantSweep("reason")`; not-found returns **404, never 403**
- [ ] Build 0 warnings; test added
