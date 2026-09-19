using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.v1.Impersonation.EndImpersonation;
using Mediator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Identity.Features.v1.Impersonation.EndImpersonation;

public static class EndImpersonationEndpoint
{
    internal static RouteHandlerBuilder MapEndImpersonationEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/impersonation/end",
            [Authorize] async Task<Results<Ok<EndImpersonationResponse>, ProblemHttpResult>>
            ([FromServices] IMediator mediator,
             CancellationToken ct) =>
            {
                var result = await mediator.Send(new EndImpersonationCommand(), ct);
                return TypedResults.Ok(result);
            })
            .WithName("EndImpersonation")
            .WithSummary("End the current acting session")
            .WithDescription("Marks the grant behind the caller's acting token (same-tenant impersonation or an exchanged operator token) as ended, so that token is rejected on its next request. Returns no token: the actor's own session was never taken away, so the client simply drops the acting one. Callable by any authenticated session carrying act_sub.")
            // Stepping *out* must stay reachable by the acting principal, which holds the target
            // user's (possibly permissionless) grants — the act_sub claim is the gate.
            .RequireAuthenticatedOnly()
            .Produces<EndImpersonationResponse>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status400BadRequest);
    }
}
