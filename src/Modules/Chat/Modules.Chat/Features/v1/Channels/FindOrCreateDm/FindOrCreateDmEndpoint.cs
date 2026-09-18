using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Chat.Contracts.Authorization;
using Boilerplate.Modules.Chat.Contracts.v1.Commands;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Chat.Features.v1.Channels.FindOrCreateDm;

public static class FindOrCreateDmEndpoint
{
    internal static RouteHandlerBuilder MapFindOrCreateDmEndpoint(this IEndpointRouteBuilder endpoints)
        => endpoints.MapPost("/dms",
                async (FindOrCreateDmCommand command, IMediator mediator, CancellationToken cancellationToken) =>
                    Results.Ok(await mediator.Send(command, cancellationToken)))
            .WithName("FindOrCreateDm")
            .WithSummary("Find existing DM or create a new DM / group DM")
            .RequirePermission(ChatPermissions.Channels.Create);
}
