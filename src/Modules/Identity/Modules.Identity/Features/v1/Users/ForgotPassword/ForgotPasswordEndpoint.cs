using Boilerplate.Modules.Identity.Contracts.v1.Users.ForgotPassword;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Identity.Features.v1.Users.ForgotPassword;

public static class ForgotPasswordEndpoint
{
    internal static RouteHandlerBuilder MapForgotPasswordEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/forgot-password", async (
            HttpRequest request,
            [FromBody] ForgotPasswordCommand command,
            IMediator mediator,
            CancellationToken cancellationToken) =>
        {
            var result = await mediator.Send(command, cancellationToken);
            return TypedResults.Ok(result);
        })
        .WithName("RequestPasswordReset")
        .WithSummary("Request password reset")
        .WithDescription("Generate a password reset token and send it via email. The tenant is taken from the '{tenant}' route segment.")
        .AllowAnonymous()
        .Produces(StatusCodes.Status200OK);
    }
}