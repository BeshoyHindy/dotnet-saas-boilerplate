using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.v1.TwoFactor;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Identity.Features.v1.TwoFactor.Enroll;

public static class EnrollTwoFactorEndpoint
{
    internal static RouteHandlerBuilder MapEnrollTwoFactorEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/2fa/enroll",
                async (IMediator mediator, CancellationToken ct) =>
                    TypedResults.Ok(await mediator.Send(new EnrollTwoFactorCommand(), ct)))
            .WithName("EnrollTwoFactor")
            .WithSummary("Begin TOTP enrollment")
            .WithDescription("Generates (or rotates) the current user's authenticator shared secret and returns it plus an otpauth:// URI for QR rendering. 2FA is NOT enabled until the caller confirms with /2fa/verify.")
            .RequireAuthorization()
            // Self-service: enrolls the caller's own authenticator.
            .RequireAuthenticatedOnly()
            // Changes the subject's own credentials — an actor must not do this on their behalf.
            .DenyWhenActing()
            .Produces<TwoFactorEnrollmentResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
    }
}
