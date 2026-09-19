using Boilerplate.BuildingBlocks.Caching;
using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.BuildingBlocks.Storage.Services;
using Boilerplate.Modules.Multitenancy.Data;
using Boilerplate.Modules.Multitenancy.Domain;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using TenantThemeService = Boilerplate.Modules.Multitenancy.Services.TenantThemeService;

namespace Multitenancy.Tests.Services;

/// <summary>
/// The default theme is the one row in <see cref="TenantDbContext"/> that is <i>not</i> per-tenant —
/// it is read from the unfiltered catalog context with no <c>TenantId</c> predicate, so it must be
/// cached and invalidated through <see cref="GlobalHybridCache"/>, not the tenant-scoped one. These
/// tests wire the real DI-composed cache stack (as <c>AddHeroCaching</c> would) rather than mocking
/// <see cref="HybridCache"/>, so a regression that quietly goes back to the tenant-scoped cache shows
/// up as a stale read, not as a changed mock expectation.
/// </summary>
public sealed class TenantThemeServiceTests
{
    private const string RootTenantId = MultitenancyConstants.Root.Id;
    private const string TenantAlpha = "alpha";
    private const string TenantBeta = "beta";

    #region GetDefaultThemeAsync — global, not tenant-scoped

    [Fact]
    public async Task GetDefaultThemeAsync_Should_Return_TheSameCachedEntry_AcrossAmbientTenants_WithOneDbLoad()
    {
        using var db = CreateSqliteDb();
        var tenantAccessor = new MutableMultiTenantContextAccessor();
        var theme = SeedTheme(db, TenantAlpha, isDefault: true, primaryColor: "#111111");

        using var harness = BuildCacheHarness(tenantAccessor);
        var sut = CreateSut(db, harness, tenantAccessor);

        tenantAccessor.SetTenant(TenantAlpha);
        var first = await sut.GetDefaultThemeAsync();
        first.LightPalette.Primary.ShouldBe("#111111");

        // Delete the row directly. If a later call re-ran the factory it would find nothing and
        // return TenantThemeDto.Default instead — so an unchanged answer proves the cache, not the
        // database, served the next two reads.
        db.TenantThemes.Remove(theme);
        await db.SaveChangesAsync();

        tenantAccessor.SetTenant(TenantBeta);
        var second = await sut.GetDefaultThemeAsync();
        second.LightPalette.Primary.ShouldBe("#111111", "the default theme is one platform-wide entry, not one per tenant.");

        tenantAccessor.SetTenant(null);
        var third = await sut.GetDefaultThemeAsync();
        third.LightPalette.Primary.ShouldBe("#111111", "GlobalHybridCache must serve the same entry with no ambient tenant at all.");
    }

    #endregion

    #region SetAsDefaultThemeAsync — root nominates another tenant, invalidates the global entry

    [Fact]
    public async Task SetAsDefaultThemeAsync_Should_Let_Root_Nominate_AnotherTenants_Theme()
    {
        using var db = CreateSqliteDb();
        var tenantAccessor = new MutableMultiTenantContextAccessor();
        SeedTheme(db, RootTenantId, isDefault: true, primaryColor: "#000000");
        SeedTheme(db, TenantAlpha, isDefault: false, primaryColor: "#abcabc");

        using var harness = BuildCacheHarness(tenantAccessor);
        var sut = CreateSut(db, harness, tenantAccessor);

        tenantAccessor.SetTenant(RootTenantId);

        // Root's caller identity is established by the root-only guard, not by EnsureAmbient — root
        // is never the tenant it is nominating. This must succeed rather than throw.
        await sut.SetAsDefaultThemeAsync(TenantAlpha);

        var alpha = await db.TenantThemes.SingleAsync(t => t.TenantId == TenantAlpha);
        alpha.IsDefault.ShouldBeTrue();
    }

    [Fact]
    public async Task SetAsDefaultThemeAsync_Should_Invalidate_TheGlobalEntry_NotJust_TheCallersTenant()
    {
        using var db = CreateSqliteDb();
        var tenantAccessor = new MutableMultiTenantContextAccessor();
        SeedTheme(db, RootTenantId, isDefault: true, primaryColor: "#000000");
        SeedTheme(db, TenantAlpha, isDefault: false, primaryColor: "#abcabc");

        using var harness = BuildCacheHarness(tenantAccessor);
        var sut = CreateSut(db, harness, tenantAccessor);

        // A third tenant, uninvolved in the change, primes the cache first.
        tenantAccessor.SetTenant(TenantBeta);
        var before = await sut.GetDefaultThemeAsync();
        before.LightPalette.Primary.ShouldBe("#000000");

        tenantAccessor.SetTenant(RootTenantId);
        await sut.SetAsDefaultThemeAsync(TenantAlpha);

        // Back in the uninvolved tenant: before #77's fix, RemoveAsync(CacheKeys.DefaultTheme) on the
        // tenant-scoped cache only ever cleared the caller's (root's) own copy, so tenant beta would
        // still read the stale default here.
        tenantAccessor.SetTenant(TenantBeta);
        var after = await sut.GetDefaultThemeAsync();
        after.LightPalette.Primary.ShouldBe("#abcabc",
            "the invalidation must go through GlobalHybridCache so every tenant sees the new default.");
    }

    [Fact]
    public async Task SetAsDefaultThemeAsync_Should_Throw_When_AmbientTenant_IsNotRoot()
    {
        using var db = CreateSqliteDb();
        var tenantAccessor = new MutableMultiTenantContextAccessor();
        SeedTheme(db, TenantAlpha, isDefault: false);

        using var harness = BuildCacheHarness(tenantAccessor);
        var sut = CreateSut(db, harness, tenantAccessor);

        tenantAccessor.SetTenant(TenantAlpha);

        await Should.ThrowAsync<ForbiddenException>(() => sut.SetAsDefaultThemeAsync(TenantAlpha));
    }

    #endregion

    #region Test doubles and helpers

    private static SqliteOwnedTenantDbContext CreateSqliteDb()
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<TenantDbContext>()
            .UseSqlite(connection)
            .Options;
        var db = new SqliteOwnedTenantDbContext(options, connection);
        db.Database.EnsureCreated();
        return db;
    }

    private static TenantTheme SeedTheme(TenantDbContext db, string tenantId, bool isDefault, string primaryColor = "#2563EB")
    {
        var theme = TenantTheme.Create(tenantId);
        theme.IsDefault = isDefault;
        theme.PrimaryColor = primaryColor;
        db.TenantThemes.Add(theme);
        db.SaveChanges();
        return theme;
    }

    private static CacheHarness BuildCacheHarness(ICacheTenantAccessor tenantAccessor)
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton(tenantAccessor);
        services.AddHeroCaching(config);

        var provider = services.BuildServiceProvider();
        return new CacheHarness(provider);
    }

    private static TenantThemeService CreateSut(TenantDbContext db, CacheHarness harness, IMultiTenantContextAccessor<AppTenantInfo> tenantAccessor)
        => new(
            harness.Cache,
            harness.GlobalCache,
            db,
            tenantAccessor,
            Substitute.For<IStorageService>(),
            NullLogger<TenantThemeService>.Instance,
            Substitute.For<ICurrentUser>());

    private sealed class CacheHarness : IDisposable
    {
        private readonly ServiceProvider _provider;

        public CacheHarness(ServiceProvider provider)
        {
            _provider = provider;
            Cache = provider.GetRequiredService<HybridCache>();
            GlobalCache = provider.GetRequiredService<GlobalHybridCache>();
        }

        public HybridCache Cache { get; }

        public GlobalHybridCache GlobalCache { get; }

        public void Dispose() => _provider.Dispose();
    }

    /// <summary>
    /// Doubles as both <see cref="IMultiTenantContextAccessor{AppTenantInfo}"/> (what
    /// <c>TenantThemeService</c> reads) and, wrapped in <c>FinbuckleCacheTenantAccessor</c>, the
    /// source of <see cref="ICacheTenantAccessor"/> (what the cache reads) — the same wiring
    /// production uses, so the two can never quietly disagree about the ambient tenant.
    /// </summary>
    private sealed class MutableMultiTenantContextAccessor : IMultiTenantContextAccessor<AppTenantInfo>, ICacheTenantAccessor
    {
        // The interface declares this non-nullable, but there genuinely is no context until a test
        // calls SetTenant — same "no ambient tenant" shape FinbuckleCacheTenantAccessor tolerates via
        // the real Finbuckle implementation returning null at runtime despite the same signature.
        private MultiTenantContext<AppTenantInfo>? _context;

        IMultiTenantContext<AppTenantInfo> IMultiTenantContextAccessor<AppTenantInfo>.MultiTenantContext => _context!;

        IMultiTenantContext IMultiTenantContextAccessor.MultiTenantContext => _context!;

        public string? TenantId => _context?.TenantInfo?.Id;

        public void SetTenant(string? tenantId)
        {
            _context = tenantId is null
                ? null
                : new MultiTenantContext<AppTenantInfo>(new AppTenantInfo { Id = tenantId });
        }
    }

    /// <summary>
    /// <c>UseSqlite(DbConnection)</c> does not take ownership of an externally-supplied connection,
    /// so disposing the context alone would leak the native handle — same reasoning as
    /// <c>Framework.Tests.Eventing.EventingTestContext</c>.
    /// </summary>
    private sealed class SqliteOwnedTenantDbContext : TenantDbContext
    {
        private readonly SqliteConnection _connection;

        public SqliteOwnedTenantDbContext(DbContextOptions<TenantDbContext> options, SqliteConnection connection)
            : base(options)
        {
            _connection = connection;
        }

        public override void Dispose()
        {
            base.Dispose();
            _connection.Dispose();
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync().ConfigureAwait(false);
            await _connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    #endregion
}
