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
    public async Task BeginAsync_Should_Open_The_Tenant_Scope_When_TenantIdProvided()
    {
        var sut = new FinbuckleEventTenantScope(_tenantScope, _provider.GetRequiredService<IServiceScopeFactory>());

        using (var handle = await sut.BeginAsync("acme"))
        {
            _tenantScope.BegunWith.ShouldHaveSingleItem().ShouldBe("acme");
            handle.Services.ShouldBeSameAs(_tenantScope.LastHandle!.Services);
            _tenantScope.LastHandle.Disposed.ShouldBeFalse();
        }

        _tenantScope.LastHandle!.Disposed.ShouldBeTrue("disposing the event scope must release the tenant scope");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BeginAsync_Should_Open_A_Plain_Scope_For_Global_Events(string? tenantId)
    {
        var sut = new FinbuckleEventTenantScope(_tenantScope, _provider.GetRequiredService<IServiceScopeFactory>());

        using var handle = await sut.BeginAsync(tenantId);

        handle.Services.ShouldNotBeNull();
        _tenantScope.BegunWith.ShouldBeEmpty("a global event must not enter any tenant");
    }

    #region Test doubles

    private sealed class RecordingTenantScope : ITenantScope
    {
        public List<string> BegunWith { get; } = [];

        public FakeHandle? LastHandle { get; private set; }

        public Task<ITenantScopeHandle> BeginAsync(string tenantId, CancellationToken cancellationToken = default)
        {
            BegunWith.Add(tenantId);
            LastHandle = new FakeHandle(new AppTenantInfo(tenantId, tenantId));
            return Task.FromResult<ITenantScopeHandle>(LastHandle);
        }

        public Task RunAsync(string tenantId, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<TResult> RunAsync<TResult>(string tenantId, Func<IServiceProvider, CancellationToken, Task<TResult>> work, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RunForEachTenantAsync(Func<AppTenantInfo, IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<AppTenantInfo>> GetTenantsAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeHandle : ITenantScopeHandle
    {
        private readonly ServiceProvider _services = new ServiceCollection().BuildServiceProvider();

        public FakeHandle(AppTenantInfo tenant) => Tenant = tenant;

        public IServiceProvider Services => _services;

        public AppTenantInfo Tenant { get; }

        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
            _services.Dispose();
        }
    }

    #endregion
}
