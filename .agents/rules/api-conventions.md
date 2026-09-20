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

Constants in `Shared/Identity/*Permissions.cs` (e.g. `IdentityPermissions`). Apply with `.RequirePermission(...)` on the endpoint. `RequiredPermissionAttribute` implements `IRequiredPermissionMetadata` — never let a duplicate of that interface appear; it silently disables **all** `.RequirePermission()` gates.

**Every endpoint must declare exactly one intent**, and the permission policy fails closed — an endpoint declaring none is denied for everyone:

- `.RequirePermission(a, b, …)` — **all-of**: the caller must hold *every* listed permission.
- `.RequireAuthenticatedOnly()` — self-service routes scoped to the caller (own profile, own password, own 2FA).
- `.AllowAnonymous()` — genuinely public (token issue/refresh, health, OpenAPI).

`EndpointAuthorizationIntentTests` (Integration.Tests) reads the running host's `EndpointDataSource` and fails the build on an endpoint that declares nothing or more than one.

## Specifications

Use `Specification<T>` (`src/BuildingBlocks/Persistence/Specifications/`) for query composition. Default `AsNoTracking = true` — see `database.md` for when tracking is required instead.

## Adding a feature (checklist)

1. Command/query + response in `Modules.{Name}.Contracts/v1/{Area}/{Feature}/`.
2. Handler in `Modules.{Name}/Features/v1/{Area}/{Feature}/`.
3. Validator in the same folder.
4. Endpoint in the same folder; wire in module `MapEndpoints()`.
5. Tests in `Tests/{Name}.Tests/` (+ integration test if it touches DB/IO).
