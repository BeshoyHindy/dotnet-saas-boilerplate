using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Webhooks.Contracts.Authorization;
using Boilerplate.Modules.Webhooks.Contracts.v1.GetWebhookDeliveries;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Webhooks.Features.v1.GetWebhookDeliveries;

public static class GetWebhookDeliveriesEndpoint
{
    internal static RouteHandlerBuilder MapGetWebhookDeliveriesEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapGet("/subscriptions/{subscriptionId:guid}/deliveries", async (
            Guid subscriptionId,
            int pageNumber,
            int pageSize,
            IMediator mediator,
            CancellationToken ct) =>
        {
            var result = await mediator.Send(new GetWebhookDeliveriesQuery(subscriptionId, pageNumber, pageSize), ct);
            return TypedResults.Ok(result);
        })
        .WithName("GetWebhookDeliveries")
        .WithSummary("List webhook deliveries for a subscription")
        .RequirePermission(WebhooksPermissions.Subscriptions.View);
    }
}
