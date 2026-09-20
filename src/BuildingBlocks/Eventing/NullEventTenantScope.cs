using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Microsoft.Extensions.DependencyInjection;

namespace Boilerplate.BuildingBlocks.Eventing;

/// <summary>
/// <see cref="IEventTenantScope"/> for a host with no multitenancy composition wired: there is one
/// database and no tenant to install, so this only opens the DI scope the handlers resolve from.
/// The multitenancy composition replaces it with a Finbuckle-backed implementation.
/// </summary>
public sealed class NullEventTenantScope : IEventTenantScope
{
    private readonly IServiceScopeFactory _scopeFactory;

    public NullEventTenantScope(IServiceScopeFactory scopeFactory) => _scopeFactory = scopeFactory;

    public async Task DispatchAsync(
        string? tenantId,
        Func<IServiceProvider, CancellationToken, Task> dispatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);

        using var scope = _scopeFactory.CreateScope();
        await dispatch(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
    }
}
