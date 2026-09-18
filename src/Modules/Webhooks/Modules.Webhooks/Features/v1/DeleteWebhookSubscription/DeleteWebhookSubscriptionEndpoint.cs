using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.Modules.Webhooks.Contracts.Authorization;
using Boilerplate.Modules.Webhooks.Contracts.v1.DeleteWebhookSubscription;
using Mediator;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Boilerplate.Modules.Webhooks.Features.v1.DeleteWebhookSubscription;

public static class DeleteWebhookSubscriptionEndpoint
{
    internal static RouteHandlerBuilder MapDeleteWebhookSubscriptionEndpoint(this IEndpointRouteBuilder endpoints)
    {
        return endpoints.MapDelete("/subscriptions/{id:guid}", async (
            Guid id,
            IMediator mediator,
            CancellationToken ct) =>
        {
            await mediator.Send(new DeleteWebhookSubscriptionCommand(id), ct);
            return TypedResults.NoContent();
        })
        .WithName("DeleteWebhookSubscription")
        .WithSummary("Delete a webhook subscription")
        .RequirePermission(WebhooksPermissions.Subscriptions.Delete)
        .Produces(StatusCodes.Status204NoContent);
    }
}
