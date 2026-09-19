using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Multitenancy.Services;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Multitenancy.Tests.Services;

/// <summary>
/// The event scope must go through <see cref="ITenantScope"/> — that is what guarantees the full
/// tenant record (connection string included) is loaded and installed before the handlers' DI scope
/// is created. It used to fabricate an id-only <c>AppTenantInfo</c> and let the bus create the scope.
/// </summary>
public sealed class FinbuckleEventTenantScopeTests
{
    private readonly RecordingTenantScope _tenantScope = new();
    private readonly ServiceProvider _provider = new ServiceCollection().BuildServiceProvider();

    [Fact]
    public async Task DispatchAsync_Should_Run_The_Dispatch_Inside_The_Tenant_Scope()
    {
        var sut = new FinbuckleEventTenantScope(_tenantScope, _provider.GetRequiredService<IServiceScopeFactory>());
        var dispatched = false;

        await sut.DispatchAsync("acme", (services, _) =>
        {
            dispatched = true;
            services.ShouldBeSameAs(_tenantScope.SuppliedServices);
            return Task.CompletedTask;
        });

        dispatched.ShouldBeTrue();
        _tenantScope.RanFor.ShouldHaveSingleItem().ShouldBe("acme");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task DispatchAsync_Should_Open_A_Plain_Scope_For_Global_Events(string? tenantId)
    {
        var sut = new FinbuckleEventTenantScope(_tenantScope, _provider.GetRequiredService<IServiceScopeFactory>());
        IServiceProvider? seen = null;

        await sut.DispatchAsync(tenantId, (services, _) =>
        {
            seen = services;
            return Task.CompletedTask;
        });

        seen.ShouldNotBeNull();
        _tenantScope.RanFor.ShouldBeEmpty("a global event must not enter any tenant");
    }

    #region Test doubles

    private sealed class RecordingTenantScope : ITenantScope
    {
        public List<string> RanFor { get; } = [];

        public IServiceProvider SuppliedServices { get; } = new ServiceCollection().BuildServiceProvider();

        public Task RunAsync(string tenantId, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(work);
            RanFor.Add(tenantId);
            return work(SuppliedServices, cancellationToken);
        }

        public Task<TResult> RunAsync<TResult>(string tenantId, Func<IServiceProvider, CancellationToken, Task<TResult>> work, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RunForEachTenantAsync(Func<AppTenantInfo, IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<AppTenantInfo> GetTenantAsync(string tenantId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AppTenantInfo>> GetTenantsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ITenantScopeHandle Begin(AppTenantInfo tenant) => throw new NotSupportedException();
    }

    #endregion
}
