using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.BuildingBlocks.Web.Idempotency;
using Boilerplate.Modules.Billing.Contracts.Authorization;
using Boilerplate.Modules.Billing.Contracts.v1.Wallets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Billing.Features.v1.Wallets.RejectTopupRequest;

public static class RejectTopupRequestEndpoint
{
    public sealed record RejectTopupRequestBody(string? Reason);

    internal static RouteHandlerBuilder MapRejectTopupRequestEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/wallet/topup-requests/{id:guid}/reject",
                async (Guid id, RejectTopupRequestBody? body, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new RejectTopupRequestCommand(id, body?.Reason), ct)))
            .WithName("RejectTopupRequest")
            .WithSummary("Reject a pending top-up request")
            .RequirePermission(BillingPermissions.Manage)
            .WithIdempotency();
    }
}
