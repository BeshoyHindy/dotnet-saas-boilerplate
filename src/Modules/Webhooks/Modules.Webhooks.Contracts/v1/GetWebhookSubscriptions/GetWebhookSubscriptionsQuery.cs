using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Webhooks.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Webhooks.Contracts.v1.GetWebhookSubscriptions;

public sealed record GetWebhookSubscriptionsQuery(int PageNumber = 1, int PageSize = 10)
    : IQuery<PagedResponse<WebhookSubscriptionDto>>;
