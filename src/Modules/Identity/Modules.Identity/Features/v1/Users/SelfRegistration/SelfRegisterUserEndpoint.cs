using Boilerplate.Modules.Identity.Contracts.v1.Users.RegisterUser;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Identity.Features.v1.Users.SelfRegistration;

public static class SelfRegisterUserEndpoint
{
    internal static RouteHandlerBuilder MapSelfRegisterUserEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/register", async (RegisterUserCommand command,
            IMediator mediator,
            CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(command, cancellationToken);
            return TypedResults.Created($"/api/v1/identity/users/{result.UserId}", result);
        })
        .WithName("SelfRegisterUser")
        .WithSummary("Self register user")
        .WithDescription("Allow a user to self-register. Anonymous; the tenant is taken from the '{tenant}' route segment.")
        // Deliberately not .WithIdempotency(): an anonymous route has no subject to bind the replay
        // partition to, so the key alone would hand a guessed key someone else's stored response
        // (#84). A sequential retry is safe without it anyway — UserRegistrationService refuses a
        // duplicate email/username with 400 rather than creating a second user.
        .AllowAnonymous()
        .Produces<RegisterUserResponse>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest);
    }
}
