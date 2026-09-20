using Boilerplate.Modules.Multitenancy.Contracts;
using Boilerplate.Modules.Multitenancy.Contracts.Dtos;
using Boilerplate.Modules.Multitenancy.Contracts.v1.GetTenantStatus;
using Mediator;

namespace Boilerplate.Modules.Multitenancy.Features.v1.GetTenantStatus;

public sealed class GetTenantStatusQueryHandler(ITenantService tenantService)
    : IQueryHandler<GetTenantStatusQuery, TenantStatusDto>
{
    public async ValueTask<TenantStatusDto> Handle(GetTenantStatusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await tenantService.GetStatusAsync(query.TenantId, cancellationToken).ConfigureAwait(false);
    }
}