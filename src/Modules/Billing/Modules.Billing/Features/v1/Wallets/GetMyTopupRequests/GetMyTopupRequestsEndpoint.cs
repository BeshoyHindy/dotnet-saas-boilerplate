using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Billing.Contracts;
using Boilerplate.Modules.Billing.Contracts.Authorization;
using Boilerplate.Modules.Billing.Contracts.v1.Wallets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Billing.Features.v1.Wallets.GetMyTopupRequests;

public static class GetMyTopupRequestsEndpoint
{
    internal static RouteHandlerBuilder MapGetMyTopupRequestsEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/wallet/topup-requests/me",
                (TopupRequestStatus? status, int pageNumber, int pageSize, IMediator mediator, CancellationToken ct) =>
                    mediator.Send(new GetMyTopupRequestsQuery(
                        status,
                        pageNumber <= 0 ? 1 : pageNumber,
                        pageSize <= 0 ? 20 : Math.Min(pageSize, 100)), ct))
            .WithName("GetMyTopupRequests")
            .WithSummary("List top-up requests for the current tenant")
            .RequirePermission(BillingPermissions.View);
    }
}
