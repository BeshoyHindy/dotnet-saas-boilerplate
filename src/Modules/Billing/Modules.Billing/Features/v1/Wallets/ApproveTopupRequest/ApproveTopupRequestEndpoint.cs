using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.BuildingBlocks.Web.Idempotency;
using Boilerplate.Modules.Billing.Contracts.Authorization;
using Boilerplate.Modules.Billing.Contracts.v1.Wallets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Billing.Features.v1.Wallets.ApproveTopupRequest;

public static class ApproveTopupRequestEndpoint
{
    public sealed record ApproveTopupRequestBody(string? Note);

    internal static RouteHandlerBuilder MapApproveTopupRequestEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/wallet/topup-requests/{id:guid}/approve",
                async (Guid id, ApproveTopupRequestBody? body, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(new ApproveTopupRequestCommand(id, body?.Note), ct)))
            .WithName("ApproveTopupRequest")
            .WithSummary("Approve a pending top-up request and issue the invoice")
            .RequirePermission(BillingPermissions.Manage)
            .WithIdempotency();
    }
}
