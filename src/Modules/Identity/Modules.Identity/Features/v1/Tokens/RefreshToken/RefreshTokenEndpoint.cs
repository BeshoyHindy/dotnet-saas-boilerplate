using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Contracts.v1.Tokens.RefreshToken;
using Finbuckle.MultiTenant.Abstractions;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Identity.Features.v1.Tokens.RefreshToken;

public static class RefreshTokenEndpoint
{
    public static RouteHandlerBuilder MapRefreshTokenEndpoint(this IEndpointRouteBuilder endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint.MapPost("/refresh",
            [AllowAnonymous] async Task<Results<Ok<RefreshTokenCommandResponse>, UnauthorizedHttpResult, ProblemHttpResult>>
            ([FromBody] RefreshTokenCommand? command,
            [FromServices] IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
            [FromServices] IMediator mediator,
            HttpContext httpContext,
            CancellationToken ct) =>
            {
                // A browser holds the token in an HttpOnly cookie it cannot read, so it cannot put it
                // in the body — the cookie is the fallback, never an override of an explicit body value.
                var refreshToken = command?.RefreshToken;
                if (string.IsNullOrWhiteSpace(refreshToken))
                {
                    refreshToken = RefreshTokenCookie.Read(httpContext) ?? string.Empty;
                }

                var tenant = tenantAccessor.MultiTenantContext?.TenantInfo?.Id;
                if (tenant is not null)
                {
                    // Anything but a successful rotation leaves the cookie holding a token the server
                    // has just refused — dead, replayed, revoked, or beaten by a concurrent caller.
                    // Clear it. The Append below wins when the rotation does succeed.
                    RefreshTokenCookie.DeleteWhenResponseStarts(httpContext, tenant);
                }

                var response = await mediator.Send(
                    new RefreshTokenCommand(command?.Token, refreshToken), ct);

                if (tenant is not null)
                {
                    RefreshTokenCookie.Append(
                        httpContext, tenant, response.RefreshToken, response.RefreshTokenExpiryTime);
                }

                return TypedResults.Ok(response);
            })
            .WithName("RefreshJwtTokens")
            .WithSummary("Refresh JWT access and refresh tokens")
            .WithDescription("Use a valid (possibly expired) access token together with a valid refresh token to obtain a new access token and a rotated refresh token. The tenant is taken from the '{tenant}' route segment. Browsers may omit the refresh token from the body and let the HttpOnly refresh cookie carry it.")
            .Produces<RefreshTokenCommandResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status500InternalServerError);
    }
}
