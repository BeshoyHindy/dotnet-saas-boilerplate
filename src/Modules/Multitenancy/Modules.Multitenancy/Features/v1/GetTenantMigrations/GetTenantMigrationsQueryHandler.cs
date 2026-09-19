using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Multitenancy.Contracts.Authorization;
using Boilerplate.Modules.Multitenancy.Contracts.Dtos;
using Boilerplate.Modules.Multitenancy.Contracts.v1.GetTenantMigrations;
using Boilerplate.Modules.Multitenancy.Data;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Boilerplate.Modules.Multitenancy.Features.v1.GetTenantMigrations;

public sealed class GetTenantMigrationsQueryHandler
    : IQueryHandler<GetTenantMigrationsQuery, IReadOnlyCollection<TenantMigrationStatusDto>>
{
    private readonly ITenantScope _tenantScope;

    public GetTenantMigrationsQueryHandler(ITenantScope tenantScope) => _tenantScope = tenantScope;

    public async ValueTask<IReadOnlyCollection<TenantMigrationStatusDto>> Handle(
        GetTenantMigrationsQuery query,
        CancellationToken cancellationToken)
    {
        var tenantMigrationStatuses = new List<TenantMigrationStatusDto>();

        // The DbContext has to be built inside the tenant scope: it captures the tenant's
        // connection string at construction, and probing the default database for every tenant
        // would report the wrong schema state for any tenant with a dedicated one.
        await _tenantScope.RunForEachTenantAsync(
            async (tenant, services, ct) =>
            {
                var tenantStatus = new TenantMigrationStatusDto
                {
                    TenantId = tenant.Id,
                    Name = tenant.Name!,
                    IsActive = tenant.IsActive,
                    ValidUpto = tenant.ValidUpto
                };

                try
                {
                    var dbContext = services.GetRequiredService<TenantDbContext>();

                    var appliedMigrations = await dbContext.Database
                        .GetAppliedMigrationsAsync(ct)
                        .ConfigureAwait(false);

                    var pendingMigrations = await dbContext.Database
                        .GetPendingMigrationsAsync(ct)
                        .ConfigureAwait(false);

                    tenantStatus.Provider = dbContext.Database.ProviderName;
                    tenantStatus.LastAppliedMigration = appliedMigrations.LastOrDefault();
                    tenantStatus.PendingMigrations = pendingMigrations.ToArray();
                    tenantStatus.HasPendingMigrations = tenantStatus.PendingMigrations.Count > 0;
                }
                // Per-tenant failure must not stop reporting on other tenants
                catch (Exception ex)
                {
                    tenantStatus.Error = ex.Message;
                }

                tenantMigrationStatuses.Add(tenantStatus);
            },
            cancellationToken).ConfigureAwait(false);

        return tenantMigrationStatuses;
    }
}