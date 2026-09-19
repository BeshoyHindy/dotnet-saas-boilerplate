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

    public Task<IEventTenantScopeHandle> BeginAsync(string? tenantId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IEventTenantScopeHandle>(new Handle(_scopeFactory.CreateScope()));

    private sealed class Handle : IEventTenantScopeHandle
    {
        private readonly IServiceScope _scope;

        public Handle(IServiceScope scope) => _scope = scope;

        public IServiceProvider Services => _scope.ServiceProvider;

        public void Dispose() => _scope.Dispose();
    }
}
