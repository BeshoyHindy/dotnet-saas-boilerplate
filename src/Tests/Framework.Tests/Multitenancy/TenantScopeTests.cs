using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Framework.Tests.Multitenancy;

/// <summary>
/// ADR-0002, "Jobs and events": the tenant scope opens BEFORE the DI scope, with the full record
/// from the store, and the previous ambient tenant comes back afterwards — success or throw.
/// </summary>
public sealed class TenantScopeTests
{
    private const string TenantId = "acme";

    [Fact]
    public async Task RunAsync_Should_Install_The_Full_Store_Record_Including_ConnectionString()
    {
        var harness = new Harness();
        harness.Store.Seed(new AppTenantInfo(TenantId, "Acme", "Host=acme-db", "admin@acme.test"));

        AppTenantInfo? seen = null;
        await harness.Sut.RunAsync(TenantId, (_, _) =>
        {
            seen = harness.Ambient.Current;
            return Task.CompletedTask;
        });

        seen.ShouldNotBeNull();
        seen!.Id.ShouldBe(TenantId);
        seen.ConnectionString.ShouldBe(
            "Host=acme-db",
            "an id-only stub would silently route the tenant's work to the default database");
        seen.AdminEmail.ShouldBe("admin@acme.test");
    }

    [Fact]
    public async Task RunAsync_Should_Create_The_DI_Scope_After_The_Tenant_Context_Is_Set()
    {
        var harness = new Harness();
        harness.Store.Seed(new AppTenantInfo(TenantId, TenantId));

        await harness.Sut.RunAsync(TenantId, (services, _) =>
        {
            // TenantCapturingService records the ambient tenant in its constructor, exactly as a
            // MultiTenantDbContext captures TenantInfo (and its connection string) at construction.
            services.GetRequiredService<TenantCapturingService>().TenantAtConstruction
                .ShouldBe(TenantId, "the DI scope must be created after the ambient tenant is installed");
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RunAsync_Should_Restore_The_Previous_Tenant_On_Success()
    {
        var harness = new Harness();
        harness.Store.Seed(new AppTenantInfo(TenantId, TenantId));
        using var outer = harness.Ambient.Enter(new AppTenantInfo("root", "root"));

        await harness.Sut.RunAsync(TenantId, (_, _) => Task.CompletedTask);

        harness.Ambient.Current!.Id.ShouldBe("root");
    }

    [Fact]
    public async Task RunAsync_Should_Restore_The_Previous_Tenant_When_The_Work_Throws()
    {
        var harness = new Harness();
        harness.Store.Seed(new AppTenantInfo(TenantId, TenantId));
        using var outer = harness.Ambient.Enter(new AppTenantInfo("root", "root"));

        await Should.ThrowAsync<InvalidOperationException>(() =>
            harness.Sut.RunAsync(TenantId, (_, _) => throw new InvalidOperationException("boom")));

        harness.Ambient.Current!.Id.ShouldBe(
            "root",
            "a leaked tenant context would silently attach the next unit of work to the wrong tenant");
    }

    [Fact]
    public async Task RunAsync_Should_Throw_For_A_Tenant_The_Store_Does_Not_Know()
    {
        var harness = new Harness();

        var ex = await Should.ThrowAsync<UnknownTenantException>(() =>
            harness.Sut.RunAsync("ghost", (_, _) => Task.CompletedTask));

        ex.Message.ShouldContain("ghost");
    }

    [Fact]
    public async Task RunAsync_Should_Dispose_The_DI_Scope_When_The_Work_Completes()
    {
        var harness = new Harness();
        harness.Store.Seed(new AppTenantInfo(TenantId, TenantId));

        TenantCapturingService? resolved = null;
        await harness.Sut.RunAsync(TenantId, (services, _) =>
        {
            resolved = services.GetRequiredService<TenantCapturingService>();
            return Task.CompletedTask;
        });

        resolved!.Disposed.ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_Generic_Should_Return_The_Work_Result()
    {
        var harness = new Harness();
        harness.Store.Seed(new AppTenantInfo(TenantId, TenantId));

        var result = await harness.Sut.RunAsync(TenantId, (_, _) => Task.FromResult(42));

        result.ShouldBe(42);
    }

    [Fact]
    public async Task Begin_Should_Restore_The_Previous_Tenant_On_Dispose()
    {
        var harness = new Harness();
        harness.Store.Seed(new AppTenantInfo(TenantId, TenantId));
        using var outer = harness.Ambient.Enter(new AppTenantInfo("root", "root"));

        var tenant = await harness.Sut.GetTenantAsync(TenantId);
        using (var handle = harness.Sut.Begin(tenant))
        {
            handle.Tenant.Id.ShouldBe(TenantId);
            harness.Ambient.Current!.Id.ShouldBe(TenantId);
            handle.Services.GetRequiredService<TenantCapturingService>().TenantAtConstruction.ShouldBe(TenantId);
        }

        harness.Ambient.Current!.Id.ShouldBe("root");
    }

    /// <summary>
    /// Regression guard, with Finbuckle's real <c>AsyncLocal</c> accessor rather than a field-backed
    /// stub. <c>Begin</c> must be synchronous: a write to an <c>AsyncLocal</c> made in the
    /// continuation of an <c>async</c> method is discarded when that method returns, so an async
    /// <c>BeginAsync</c> hands back a handle whose tenant is not ambient for the caller — and the
    /// caller's first scoped DbContext is then built with no tenant at all. Hangfire's job activator
    /// is exactly that caller.
    /// </summary>
    [Fact]
    public async Task Begin_Should_Make_The_Tenant_Ambient_In_The_Callers_Own_Flow()
    {
        var harness = new Harness(useRealAsyncLocalAccessor: true);
        harness.Store.Seed(new AppTenantInfo(TenantId, TenantId));

        var tenant = await harness.Sut.GetTenantAsync(TenantId);

        using var handle = harness.Sut.Begin(tenant);

        harness.Ambient.Current.ShouldNotBeNull(
            "the ambient tenant must survive into the caller's frame, not stay inside an async method");
        harness.Ambient.Current!.Id.ShouldBe(TenantId);
    }

    /// <summary>The same hazard from the other side: the work callback runs inside RunAsync, so it
    /// does see the ambient tenant even with the real AsyncLocal accessor.</summary>
    [Fact]
    public async Task RunAsync_Should_Make_The_Tenant_Ambient_For_The_Work_With_The_Real_AsyncLocal()
    {
        var harness = new Harness(useRealAsyncLocalAccessor: true);
        harness.Store.Seed(new AppTenantInfo(TenantId, TenantId));

        await harness.Sut.RunAsync(TenantId, (services, _) =>
        {
            harness.Ambient.Current!.Id.ShouldBe(TenantId);
            services.GetRequiredService<TenantCapturingService>().TenantAtConstruction.ShouldBe(TenantId);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public async Task RunForEachTenantAsync_Should_Run_Once_Per_Tenant_Under_That_Tenant()
    {
        var harness = new Harness();
        harness.Store.Seed(new AppTenantInfo("root", "root"));
        harness.Store.Seed(new AppTenantInfo(TenantId, TenantId));

        var visited = new List<string>();
        await harness.Sut.RunForEachTenantAsync((tenant, _, _) =>
        {
            visited.Add(tenant.Id!);
            harness.Ambient.Current!.Id.ShouldBe(tenant.Id);
            return Task.CompletedTask;
        });

        visited.ShouldBe(["root", TenantId], ignoreOrder: true);
        harness.Ambient.Current.ShouldBeNull();
    }

    [Fact]
    public async Task GetTenantsAsync_Should_Return_The_Whole_Catalog()
    {
        var harness = new Harness();
        harness.Store.Seed(new AppTenantInfo("root", "root"));
        harness.Store.Seed(new AppTenantInfo(TenantId, TenantId));

        (await harness.Sut.GetTenantsAsync()).Select(t => t.Id).ShouldBe(["root", TenantId], ignoreOrder: true);
    }

    #region Harness

    private sealed class Harness
    {
        public Harness(bool useRealAsyncLocalAccessor = false)
        {
            // The stub is a plain field, which makes assertions easy but cannot catch execution-context
            // mistakes; the real accessor is an AsyncLocal, which can.
            var accessor = useRealAsyncLocalAccessor
                ? new AsyncLocalMultiTenantContextAccessor<AppTenantInfo>()
                : (IMultiTenantContextAccessor<AppTenantInfo>)Accessor;
            var setter = useRealAsyncLocalAccessor
                ? (IMultiTenantContextSetter)accessor
                : Accessor;

            Ambient = new AmbientTenantContext(accessor, setter);

            var services = new ServiceCollection();
            services.AddSingleton(accessor);
            services.AddSingleton(setter);
            services.AddSingleton<AmbientTenantContext>(Ambient);
            services.AddSingleton<IMultiTenantStore<AppTenantInfo>>(Store);
            services.AddScoped<TenantCapturingService>();

            Provider = services.BuildServiceProvider();
            Sut = new TenantScope(Provider.GetRequiredService<IServiceScopeFactory>(), Ambient);
        }

        public StubAccessor Accessor { get; } = new();

        public AmbientTenantContext Ambient { get; }

        public FakeTenantStore Store { get; } = new();

        public ServiceProvider Provider { get; }

        public TenantScope Sut { get; }
    }

    /// <summary>Records the ambient tenant at construction, the way a MultiTenantDbContext does.</summary>
    private sealed class TenantCapturingService : IDisposable
    {
        public TenantCapturingService(IMultiTenantContextAccessor<AppTenantInfo> accessor)
            => TenantAtConstruction = accessor.MultiTenantContext.TenantInfo?.Id;

        public string? TenantAtConstruction { get; }

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeTenantStore : IMultiTenantStore<AppTenantInfo>
    {
        private readonly List<AppTenantInfo> _tenants = [];

        public void Seed(AppTenantInfo tenant) => _tenants.Add(tenant);

        public Task<bool> AddAsync(AppTenantInfo tenantInfo)
        {
            _tenants.Add(tenantInfo);
            return Task.FromResult(true);
        }

        public Task<AppTenantInfo?> GetAsync(string id) =>
            Task.FromResult(_tenants.Find(t => t.Id == id));

        public Task<IEnumerable<AppTenantInfo>> GetAllAsync() =>
            Task.FromResult<IEnumerable<AppTenantInfo>>(_tenants.ToList());

        public Task<IEnumerable<AppTenantInfo>> GetAllAsync(int take, int skip) =>
            Task.FromResult<IEnumerable<AppTenantInfo>>(_tenants.Skip(skip).Take(take).ToList());

        public Task<AppTenantInfo?> GetByIdentifierAsync(string identifier) =>
            Task.FromResult(_tenants.Find(t => t.Identifier == identifier));

        public Task<bool> RemoveAsync(string identifier) =>
            Task.FromResult(_tenants.RemoveAll(t => t.Identifier == identifier) > 0);

        public Task<bool> UpdateAsync(AppTenantInfo tenantInfo) => Task.FromResult(true);
    }

#pragma warning disable S2376 // Finbuckle's IMultiTenantContextSetter is a set-only contract.
    private sealed class StubAccessor : IMultiTenantContextAccessor<AppTenantInfo>, IMultiTenantContextSetter
    {
        private IMultiTenantContext<AppTenantInfo> _context = new MultiTenantContext<AppTenantInfo>(null!);

        public IMultiTenantContext<AppTenantInfo> MultiTenantContext => _context;

        IMultiTenantContext IMultiTenantContextAccessor.MultiTenantContext => _context;

        IMultiTenantContext IMultiTenantContextSetter.MultiTenantContext
        {
            set => _context = (IMultiTenantContext<AppTenantInfo>)value;
        }
    }
#pragma warning restore S2376

    #endregion
}
