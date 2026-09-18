using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Billing.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Billing.Contracts.v1.Wallets;

public sealed record GetMyTopupRequestsQuery(
    TopupRequestStatus? Status = null,
    int PageNumber = 1,
    int PageSize = 20) : IQuery<PagedResponse<TopupRequestDto>>;
