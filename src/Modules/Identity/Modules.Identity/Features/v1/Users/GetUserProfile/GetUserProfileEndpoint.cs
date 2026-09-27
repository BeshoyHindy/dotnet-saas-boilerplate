using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.BuildingBlocks.Shared.Identity.Claims;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.v1.Users.GetUserProfile;
using Boilerplate.Modules.Identity.Services;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Security.Claims;

namespace Boilerplate.Modules.Identity.Features.v1.Users.GetUserProfile;

public static class GetUserProfileEndpoint
{
    internal static RouteHandlerBuilder MapGetMeEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/profile", async (ClaimsPrincipal user, HttpResponse response, IMediator mediator, CancellationToken cancellationToken) =>
        {
            if (user.GetUserId() is not { } userId || string.IsNullOrEmpty(userId))
            {
                throw new UnauthorizedException();
            }

            var profile = await mediator.Send(new GetCurrentUserProfileQuery(userId), cancellationToken);

            // The version the caller echoes in If-Match on PUT /profile (#107).
            if (ProfileETag.Format(profile.ConcurrencyStamp) is { } etag)
            {
                response.Headers.ETag = etag;
            }

            return TypedResults.Ok(profile);
        })
        .WithName("GetCurrentUserProfile")
        .WithSummary("Get current user profile")
        .WithDescription("Retrieve the authenticated user's profile from the access token. The strong ETag response header is the profile's version; send it back in If-Match on PUT /profile.")
        .RequireAuthorization()
        // Self-service: returns the caller's own profile, resolved from their token.
        .RequireAuthenticatedOnly()
        .Produces<UserDto>(StatusCodes.Status200OK)
        .Produces(StatusCodes.Status401Unauthorized);
    }
}