using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Boilerplate.Modules.Multitenancy.Services;

/// <summary>
/// Finbuckle-backed <see cref="IEventTenantScope"/>, implemented entirely on top of
/// <see cref="ITenantScope"/>: the event's tenant is loaded from the store as a full record and
/// installed before the handlers' DI scope exists, so a handler's DbContext is built with the
/// tenant filter already pointed at the right tenant.
///
/// The record, not <c>new AppTenantInfo(tenantId, tenantId)</c>: an id-only stub would dispatch an
/// event for a tenant the store no longer knows, or one an operator has deactivated. Going through
/// <see cref="ITenantScope"/> makes both fail closed, and it is cache-first, so the record costs a
/// cache read rather than a catalog query.
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
