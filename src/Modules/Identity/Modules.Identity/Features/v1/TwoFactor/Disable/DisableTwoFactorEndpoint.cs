using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Identity.Contracts.v1.TwoFactor;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Identity.Features.v1.TwoFactor.Disable;

public static class DisableTwoFactorEndpoint
{
    internal static RouteHandlerBuilder MapDisableTwoFactorEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/2fa/disable",
                async (DisableTwoFactorCommand command, IMediator mediator, CancellationToken ct) =>
                    TypedResults.Ok(new { success = await mediator.Send(command, ct) }))
            .WithName("DisableTwoFactor")
            .WithSummary("Disable TOTP for the current user")
            .WithDescription("Turns off 2FA after confirming the current password. Also rotates the authenticator secret so a re-enroll starts fresh.")
            .RequireAuthorization()
            // Self-service: acts on the caller's own account and re-confirms their password.
            .RequireAuthenticatedOnly()
            // Disables the subject's own 2FA — an actor must not do this on their behalf.
            .DenyWhenActing()
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
    }
}
