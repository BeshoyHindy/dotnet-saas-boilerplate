using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Multitenancy.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Multitenancy.Contracts.v1.GetTenants;

public sealed class GetTenantsQuery : IPagedQuery, IQuery<PagedResponse<TenantDto>>
{
    public int? PageNumber { get; set; }

    public int? PageSize { get; set; }

    public string? Sort { get; set; }
}