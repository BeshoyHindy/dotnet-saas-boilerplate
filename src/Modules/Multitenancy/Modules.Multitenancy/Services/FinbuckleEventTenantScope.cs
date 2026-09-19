using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Boilerplate.Modules.Multitenancy.Services;

/// <summary>
/// Finbuckle-backed <see cref="IEventTenantScope"/>, implemented entirely on top of
/// <see cref="ITenantScope"/>: the event's tenant is loaded from the store as a full record and
/// installed before the handlers' DI scope exists, so a handler's DbContext picks up that tenant's
/// own connection string instead of the default one.
///
/// It used to fabricate <c>new AppTenantInfo(tenantId, tenantId)</c> — identity only. That was
/// enough for the row-level filter in the shared-database model and silently wrong for a tenant
/// with a dedicated database: the handler read and wrote the default one.
/// </summary>
public sealed class FinbuckleEventTenantScope : IEventTenantScope
{
    private readonly ITenantScope _tenantScope;
    private readonly IServiceScopeFactory _scopeFactory;

    public FinbuckleEventTenantScope(ITenantScope tenantScope, IServiceScopeFactory scopeFactory)
    {
        _tenantScope = tenantScope;
        _scopeFactory = scopeFactory;
    }

    public async Task DispatchAsync(
        string? tenantId,
        Func<IServiceProvider, CancellationToken, Task> dispatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        if (string.IsNullOrWhiteSpace(tenantId))
        {
            // Global event — the bus has already checked the type declares itself one. Leave the
            // ambient context alone and just give the handlers a scope.
            using var scope = _scopeFactory.CreateScope();
            await dispatch(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _tenantScope.RunAsync(tenantId, dispatch, cancellationToken).ConfigureAwait(false);
    }
}
