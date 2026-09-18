using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.BuildingBlocks.Shared.Persistence;
using Boilerplate.Modules.Multitenancy.Contracts.Dtos;
using Boilerplate.Modules.Multitenancy.Contracts.v1.GetTenants;

namespace Boilerplate.Modules.Multitenancy.Contracts;

public interface ITenantService
{
    Task<PagedResponse<TenantDto>> GetAllAsync(GetTenantsQuery query, CancellationToken cancellationToken);

    Task<bool> ExistsWithIdAsync(string id, CancellationToken cancellationToken = default);

    Task<bool> ExistsWithNameAsync(string name, CancellationToken cancellationToken = default);

    Task<TenantStatusDto> GetStatusAsync(string id, CancellationToken cancellationToken = default);

    Task<string> CreateAsync(string id, string name, string? connectionString, string adminEmail, string? issuer, DateTime validUpto, CancellationToken cancellationToken);

    Task<string> ActivateAsync(string id, CancellationToken cancellationToken);

    Task<string> DeactivateAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Extends the tenant's validity by <paramref name="months"/>, stacking on remaining time (no
    /// backdating). Returns the validity window applied.
    /// </summary>
    Task<(DateTime PeriodStartUtc, DateTime ValidUpto)> RenewAsync(
        string id, int months, CancellationToken cancellationToken = default);

    /// <summary>
    /// Operator override that sets the tenant's validity to an explicit date — for comps, support
    /// extensions, or immediate expiry. Unlike <see cref="RenewAsync"/> this may move the date
    /// backward. Returns the applied <c>ValidUpto</c> (UTC).
    /// </summary>
    Task<DateTime> AdjustValidityAsync(string id, DateTime validUpto, CancellationToken cancellationToken = default);

    Task MigrateTenantAsync(AppTenantInfo tenant, CancellationToken cancellationToken);

    Task SeedTenantAsync(AppTenantInfo tenant, CancellationToken cancellationToken);
}