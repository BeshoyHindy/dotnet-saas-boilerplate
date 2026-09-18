using Boilerplate.Modules.Auditing.Contracts.Authorization;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Auditing.Contracts.Dtos;
using Boilerplate.Modules.Auditing.Contracts.v1.GetSecurityAudits;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Auditing.Features.v1.GetSecurityAudits;

public static class GetSecurityAuditsEndpoint
{
    public static RouteHandlerBuilder MapGetSecurityAuditsEndpoint(this IEndpointRouteBuilder group)
    {
        return group.MapGet(
                "/security",
                async ([AsParameters] GetSecurityAuditsQuery query, IMediator mediator, CancellationToken cancellationToken) =>
                    TypedResults.Ok(await mediator.Send(query, cancellationToken)))
            .WithName("GetSecurityAudits")
            .WithSummary("Get security-related audit events")
            .WithDescription("Retrieve security audit events such as login, logout, and permission denials.")
            .RequirePermission(AuditingPermissions.AuditTrails.View)
            .Produces<IEnumerable<AuditSummaryDto>>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
    }
}