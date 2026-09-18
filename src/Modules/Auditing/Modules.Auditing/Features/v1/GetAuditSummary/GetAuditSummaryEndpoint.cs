using Boilerplate.Modules.Auditing.Contracts.Authorization;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Auditing.Contracts.Dtos;
using Boilerplate.Modules.Auditing.Contracts.v1.GetAuditSummary;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Auditing.Features.v1.GetAuditSummary;

public static class GetAuditSummaryEndpoint
{
    public static RouteHandlerBuilder MapGetAuditSummaryEndpoint(this IEndpointRouteBuilder group)
    {
        return group.MapGet(
                "/summary",
                async ([AsParameters] GetAuditSummaryQuery query, IMediator mediator, CancellationToken cancellationToken) =>
                    TypedResults.Ok(await mediator.Send(query, cancellationToken)))
            .WithName("GetAuditSummary")
            .WithSummary("Get audit summary")
            .WithDescription("Retrieve aggregate counts of audit events by type, severity, source, and tenant.")
            .RequirePermission(AuditingPermissions.AuditTrails.View)
            .Produces<AuditSummaryAggregateDto>(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
    }
}