# API conventions

Read before adding endpoints, commands/queries, validators, or error handling.

## Endpoints

Static extension methods on `IEndpointRouteBuilder`, returning `RouteHandlerBuilder`. The handler delegates to Mediator. Gate with `.RequirePermission(...)`.

```csharp
public static class RegisterUserEndpoint
{
    internal static RouteHandlerBuilder MapRegisterUserEndpoint(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapPost("/register", (RegisterUserCommand command,
                IMediator mediator, CancellationToken cancellationToken) =>
                mediator.Send(command, cancellationToken))
            .WithName("RegisterUser")
            .WithSummary("Register user")
            .RequirePermission(IdentityPermissions.Users.Create);
}
```

- **Always accept and forward `CancellationToken`** to `mediator.Send`. ASP.NET injects it.
- Wire each endpoint in the module's `MapEndpoints()`. Endpoints group under `api/v{version:apiVersion}/{module}`.
- Use `TypedResults` / `.Produces<T>(...)` for accurate OpenAPI. Add `.WithIdempotency()` on POSTs that must be replay-safe.

## CQRS

- **Commands/Queries** live in `Modules.{Name}.Contracts` — implement `ICommand<TResponse>` / `IQuery<TResponse>`. Records preferred.
- **Handlers** live in `Modules.{Name}/Features/` — `public sealed`, implement `ICommandHandler<T,TResponse>` / `IQueryHandler<T,TResponse>`, return `ValueTask<T>`, `.ConfigureAwait(false)` on awaits.
- Paginated queries implement `IPagedQuery` (`PageNumber`, `PageSize`, `Sort`) and return `PagedResponse<T>`.

## Validation

FluentValidation, auto-registered by `ModuleLoader`. Name `{Command}Validator`. Live in the same feature folder.

- **Every command handler needs a validator; every paginated query handler needs one too.** Enforced by `Architecture.Tests` (`HandlerValidatorPairingTests`). A handler legitimately without rules can be added to that test's known-missing allowlist, but prefer writing the validator.
- Validators run via the `ValidationBehavior<,>` Mediator pipeline before the handler.

## Exceptions → ProblemDetails

Throw framework exception types; the global handler converts to RFC 9457 `ProblemDetails`:

| Throw | HTTP |
|---|---|
| `NotFoundException` | 404 |
| `ForbiddenException` | 403 |
| `UnauthorizedException` | 401 |
| `CustomException(msg, errors?, HttpStatusCode)` | as specified (default 400) |

Don't catch broadly to swallow. Background loops may `catch (Exception)` to stay alive, but must **log with context** and exclude `OperationCanceledException` (filtered catch or a preceding `catch (OperationCanceledException)`).

## Permissions

Constants live in each module's Contracts project: `Modules.{X}.Contracts/Authorization/{X}Permissions.cs` (e.g. `IdentityPermissions`, `NotificationPermissions`) — one nested class per resource with a `Resource` string and `Permissions.{Resource}.{Action}` constants, plus an `All` list registered via `AddPermissions(...)`. `IsBasic`/`IsRoot` and how new permissions reach roles: `docs/new-project-guide.md` §3. Apply with `.RequirePermission(...)` on the endpoint. `RequiredPermissionAttribute` implements `IRequiredPermissionMetadata` — never let a duplicate of that interface appear; it silently disables **all** `.RequirePermission()` gates.

**Every endpoint must declare exactly one intent**, and the permission policy fails closed — an endpoint declaring none is denied for everyone:

- `.RequirePermission(a, b, …)` — **all-of**: the caller must hold *every* listed permission.
- `.RequireAuthenticatedOnly()` — self-service routes scoped to the caller (own profile, own password, own 2FA).
- `.AllowAnonymous()` — genuinely public (token issue/refresh, health, OpenAPI).

`EndpointAuthorizationIntentTests` (Integration.Tests, so Docker) reads the running host's `EndpointDataSource` and fails on an endpoint that declares nothing or more than one. The build and Architecture.Tests stay green on such an endpoint — run the integration suite before you push.

**A request never names a tenant** (ADR-0002). A command or query an endpoint binds or builds may not carry a property with "tenant" in its name; the tenant is the token's `tenant` claim, already ambient in the handler. `CallerNeverNamesATenantTests` (Architecture.Tests, no Docker) fails on one, and its allow-list holds only root-only operations on a tenant and the `{tenant}` segment of the anonymous auth routes, each with its reason. If a handler copies a tenant id onto a row anyway, Finbuckle refuses the save and the caller gets a 400 that names neither the exception nor the tenant.

## Specifications

Use `Specification<T>` (`src/BuildingBlocks/Persistence/Specifications/`) for query composition. Default `AsNoTracking = true` — see `database.md` for when tracking is required instead.

## Adding a feature (checklist)

1. Command/query + response in `Modules.{Name}.Contracts/v1/[{Area}/]{Feature}/` (the `{Area}/` level only in a module with several areas — see `architecture.md`).
2. Handler in `Modules.{Name}/Features/v1/[{Area}/]{Feature}/`.
3. Validator in the same folder.
4. Endpoint in the same folder; wire in module `MapEndpoints()`.
5. Tests: integration tests in `Tests/Integration.Tests/Tests/{Name}/` are the default, including the tenant-sweep entry for a new noun (`integration-testing.md`). A unit test goes in `Tests/{Name}.Tests/` if the module has one; a product module's unit project is optional (`docs/new-project-guide.md` §3 gives its three steps).
