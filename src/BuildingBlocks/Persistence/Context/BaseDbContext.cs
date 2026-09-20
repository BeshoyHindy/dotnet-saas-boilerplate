using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.EntityFrameworkCore;
using Boilerplate.BuildingBlocks.Core.Domain;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Microsoft.EntityFrameworkCore;

namespace Boilerplate.BuildingBlocks.Persistence.Context;

/// <summary>
/// Base database context with multi-tenancy and soft delete support.
/// </summary>
/// <param name="multiTenantContextAccessor">Accessor for multi-tenant context information.</param>
/// <param name="options">Database context options.</param>
public class BaseDbContext(IMultiTenantContextAccessor<AppTenantInfo> multiTenantContextAccessor,
    DbContextOptions options)
    : MultiTenantDbContext(multiTenantContextAccessor, options)
{
    /// <summary>
    /// Configures the model by applying global query filters for soft delete functionality.
    /// </summary>
    /// <param name="modelBuilder">The model builder used to configure the database schema.</param>
    /// <exception cref="ArgumentNullException">Thrown when modelBuilder is null.</exception>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.AppendGlobalQueryFilter<ISoftDeletable>(QueryFilters.SoftDelete, s => !s.IsDeleted);
        base.OnModelCreating(modelBuilder);
        // Default-on tenant isolation: entities not marked IGlobalEntity get IsMultiTenant().
        // Subclasses must call base.OnModelCreating AFTER ApplyConfigurationsFromAssembly so per-entity configs are in place.
        modelBuilder.ApplyTenantIsolationByDefault();
    }

    /// <summary>
    /// Saves all changes made in this context to the database with tenant overwrite mode.
    /// </summary>
    /// <param name="cancellationToken">A cancellation token to cancel the save operation.</param>
    /// <returns>The number of state entries written to the database.</returns>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        TenantNotSetMode = TenantNotSetMode.Overwrite;
        int result = await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}