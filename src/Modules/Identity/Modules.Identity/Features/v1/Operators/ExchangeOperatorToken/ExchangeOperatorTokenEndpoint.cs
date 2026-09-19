using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Identity.Contracts.v1.Operators.ExchangeOperatorToken;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Identity.Features.v1.Operators.ExchangeOperatorToken;

public static class ExchangeOperatorTokenEndpoint
{
    internal static RouteHandlerBuilder MapExchangeOperatorTokenEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/operator/token-exchange",
            async Task<Results<Ok<OperatorTokenExchangeResponse>, ProblemHttpResult>>
            ([FromBody] ExchangeOperatorTokenCommand command,
             [FromServices] IMediator mediator,
             CancellationToken ct) =>
            {
                var response = await mediator.Send(command, ct);
                return TypedResults.Ok(response);
            })
            .WithName("ExchangeOperatorToken")
            .WithSummary("Exchange an operator token for a target tenant")
            .WithDescription(
                "ADR-0002. Root operators cross tenants by exchanging tokens, never by header. Returns a " +
                "short-lived, access-only token whose `tenant` claim is the target tenant and whose subject " +
                "is a real user of that tenant (targetUserId, else the tenant's admin), carrying act_sub/" +
                "act_tenant for the operator. No refresh token, no session row, no cookie. The exchange is " +
                "audited and revocable through the impersonation grant list (same jti revocation path). " +
                "Requesting more than the configured maximum clamps the lifetime rather than failing.")
            // Root-only permission (IsRoot in the catalog), plus a root-tenant check in the handler:
            // a non-root caller is 403 even if a mis-seeded role handed them the permission.
            .RequirePermission(SystemPermissions.Platform.CrossTenantImpersonate)
            .Produces<OperatorTokenExchangeResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
    }
}
