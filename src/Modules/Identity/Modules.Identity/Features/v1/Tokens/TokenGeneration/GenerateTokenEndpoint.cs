using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.v1.Tokens.TokenGeneration;
using Finbuckle.MultiTenant.Abstractions;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Identity.Features.v1.Tokens.TokenGeneration;

public static class GenerateTokenEndpoint
{
    public static RouteHandlerBuilder MapGenerateTokenEndpoint(this IEndpointRouteBuilder endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint.MapPost("/token",
            [AllowAnonymous] async Task<Results<Ok<TokenResponse>, UnauthorizedHttpResult>>
            ([FromBody] GenerateTokenCommand command,
            [FromServices] IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
            [FromServices] IMediator mediator,
            HttpContext httpContext,
            CancellationToken ct) =>
            {
                // The tenant comes from the {tenant} route segment via tenant resolution — never from a
                // caller-supplied header. An unknown tenant resolves to null and the command fails 401,
                // which is exactly what a wrong password returns.
                var tenant = tenantAccessor.MultiTenantContext?.TenantInfo?.Id;

                // No `X-Client-App` check: that header kept root operators out of the tenant dashboard
                // while there were two client apps. There is one console now, and the operator screens
                // inside it sit behind permissions (ADR-0004), so every account signs in the same way.
                var token = await mediator.Send(command, ct);
                if (token is null)
                {
                    return TypedResults.Unauthorized();
                }

                // Browsers get the refresh token as a cookie they cannot read; the body copy stays
                // for native and server-side clients (ADR-0002).
                RefreshTokenCookie.Append(httpContext, tenant!, token.RefreshToken, token.RefreshTokenExpiresAt);
                return TypedResults.Ok(token);
            })
            .WithName("IssueJwtTokens")
            .WithSummary("Issue JWT access and refresh tokens")
            .WithDescription("Submit credentials to receive a JWT access token and a refresh token. The tenant is taken from the '{tenant}' route segment.")
            .Produces<TokenResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status500InternalServerError);
    }
}
