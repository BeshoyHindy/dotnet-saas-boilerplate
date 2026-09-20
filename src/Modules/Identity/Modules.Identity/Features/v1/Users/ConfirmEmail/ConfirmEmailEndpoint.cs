using Boilerplate.Modules.Identity.Contracts.v1.Users.ConfirmEmail;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Identity.Features.v1.Users.ConfirmEmail;

public static class ConfirmEmailEndpoint
{
    internal static RouteHandlerBuilder MapConfirmEmailEndpoint(this IEndpointRouteBuilder endpoints)
    {
        // `tenant` binds from the group's {tenant} route segment (same name), which is also what
        // tenant resolution used — the handler and the ambient tenant can never disagree.
        return endpoints.MapGet("/confirm-email", async (
            [FromRoute] string tenant,
            [FromQuery] string userId,
            [FromQuery] string code,
            IMediator mediator,
            CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(new ConfirmEmailCommand(userId, code, tenant), cancellationToken);
            return TypedResults.Ok(result);
        })
        .WithName("ConfirmEmail")
        .WithSummary("Confirm user email")
        .WithDescription("Confirm a user's email address. The tenant is taken from the '{tenant}' route segment.")
        .AllowAnonymous()
        .Produces(StatusCodes.Status200OK);
    }
}