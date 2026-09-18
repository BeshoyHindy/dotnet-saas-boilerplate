using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.BuildingBlocks.Web.Idempotency;
using Boilerplate.Modules.Billing.Contracts.Authorization;
using Boilerplate.Modules.Billing.Contracts.v1.Wallets;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Billing.Features.v1.Wallets.CreateTopupRequest;

public static class CreateTopupRequestEndpoint
{
    internal static RouteHandlerBuilder MapCreateTopupRequestEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapPost("/wallet/topup-requests",
                async (CreateTopupRequestCommand command, IMediator mediator, CancellationToken ct) =>
                    Results.Ok(await mediator.Send(command, ct)))
            .WithName("CreateTopupRequest")
            .WithSummary("Submit a wallet top-up request for the current tenant")
            .RequirePermission(BillingPermissions.View)
            .WithIdempotency();
    }
}
