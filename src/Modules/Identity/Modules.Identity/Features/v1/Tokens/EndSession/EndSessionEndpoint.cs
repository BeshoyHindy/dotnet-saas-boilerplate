using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Contracts.v1.Tokens.EndSession;
using Finbuckle.MultiTenant.Abstractions;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Identity.Features.v1.Tokens.EndSession;

public static class EndSessionEndpoint
{
    /// <summary>
    /// <c>POST /api/v1/tenants/{tenant}/auth/logout</c> — the route keeps the word every client
    /// already uses; the types are named for what they do to the session store.
    /// </summary>
    public static RouteHandlerBuilder MapEndSessionEndpoint(this IEndpointRouteBuilder endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        return endpoint.MapPost("/logout",
            // AllowAnonymous, deliberately. The whole point of this endpoint is to work when the
            // access token is gone — that is the state a signing-out browser is in, holding only
            // the HttpOnly refresh cookie. Requiring authentication would leave exactly those
            // sessions un-revocable. It is not an open door: the only thing an anonymous caller can
            // do here is end a session they already hold the refresh token for, and the response is
            // 204 either way, so it reveals nothing about which tokens exist.
            [AllowAnonymous] async Task<NoContent>
            ([FromBody] EndSessionCommand? command,
            [FromServices] IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor,
            [FromServices] IMediator mediator,
            HttpContext httpContext,
            CancellationToken ct) =>
            {
                var tenant = tenantAccessor.MultiTenantContext?.TenantInfo?.Id;
                if (tenant is not null)
                {
                    // Queued before anything can fail, so the cookie is cleared even if the
                    // revocation throws. Leaving it behind is the bug this endpoint exists to fix:
                    // the SPA cannot delete an HttpOnly cookie, and /auth/refresh accepts it alone.
                    RefreshTokenCookie.DeleteWhenResponseStarts(httpContext, tenant);
                }

                // The cookie's Path is the refresh route, so a browser does not send it *here* —
                // which is why the client passes the token in the body. Reading it anyway costs
                // nothing and covers direct API callers that do send it.
                var refreshToken = command?.RefreshToken;
                if (string.IsNullOrWhiteSpace(refreshToken))
                {
                    refreshToken = RefreshTokenCookie.Read(httpContext);
                }

                await mediator.Send(new EndSessionCommand(refreshToken), ct);

                return TypedResults.NoContent();
            })
            .WithName("Logout")
            .WithSummary("End the current session")
            .WithDescription("Revokes the session behind the caller's access token ('sid' claim) or, failing that, the session the supplied refresh token belongs to, and clears the refresh cookie. Always returns 204 so it cannot be used to probe tokens.")
            .Produces(StatusCodes.Status204NoContent);
    }
}
