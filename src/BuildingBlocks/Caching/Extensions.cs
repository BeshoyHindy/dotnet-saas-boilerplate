using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StackExchange.Redis;

namespace Boilerplate.BuildingBlocks.Caching;

/// <summary>
/// DI extensions for the HybridCache-backed caching building block.
/// </summary>
public static class Extensions
{
    private const string MissingTenantAccessorMessage =
        "Cache entries are tenant-scoped (ADR-0002) but no ICacheTenantAccessor is registered. Compose the " +
        "Multitenancy module — it registers the Finbuckle-backed accessor — or call " +
        "AddHeroCaching(configuration, singleTenant: true) if this host genuinely serves a single tenant. " +
        "The choice is deliberate on purpose: there is no silent default.";

    /// <summary>
    /// Registers <see cref="HybridCache"/> layered over either Redis (when
    /// <see cref="CachingOptions.Redis"/> is set) or an in-memory distributed cache fallback,
    /// then wraps it with <see cref="ObservableHybridCache"/> so every operation emits OTel
    /// metrics and activities via <see cref="Telemetry.CachingTelemetry"/>, and finally with
    /// <see cref="TenantScopedHybridCache"/> so every key and tag is scoped to the ambient tenant.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Configuration carrying the <c>CachingOptions</c> section.</param>
    /// <param name="singleTenant">
    /// Set to <see langword="true"/> only for a host composed without the Multitenancy module: it
    /// registers <see cref="SingleTenantCacheTenantAccessor"/> so every key lands in one fixed
    /// partition. Left <see langword="false"/>, resolving the cache without a registered
    /// <see cref="ICacheTenantAccessor"/> fails loudly rather than falling back to an untenanted key.
    /// </param>
    /// <remarks>
    /// HybridCache provides stampede-protected <c>GetOrCreateAsync</c>, built-in L1 (in-process)
    /// + L2 (distributed) layering, and logical tag-based invalidation. Consumers inject
    /// <see cref="HybridCache"/> for tenant-scoped entries and <see cref="GlobalHybridCache"/> for
    /// the explicitly tenant-less ones; both decorators are otherwise transparent.
    /// </remarks>
    public static IServiceCollection AddHeroCaching(
        this IServiceCollection services,
        IConfiguration configuration,
        bool singleTenant = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<CachingOptions>()
            .BindConfiguration(nameof(CachingOptions));

        var cacheOptions = configuration.GetSection(nameof(CachingOptions)).Get<CachingOptions>() ?? new CachingOptions();

        // L2: Redis if configured, in-memory distributed cache otherwise. StackExchangeRedis 9.0+
        // implements IBufferDistributedCache, which HybridCache uses for zero-copy reads.
        if (string.IsNullOrEmpty(cacheOptions.Redis))
        {
            services.AddDistributedMemoryCache();
        }
        else
        {
            // Connect once and share the multiplexer across the Redis cache, Data Protection key
            // persistence, and future consumers — one connection pool per host, not per feature.
            var redisConfig = ConfigurationOptions.Parse(cacheOptions.Redis);
            redisConfig.AbortOnConnectFail = false;
            if (cacheOptions.EnableSsl.HasValue)
            {
                redisConfig.Ssl = cacheOptions.EnableSsl.Value;
            }
            var sharedMultiplexer = ConnectionMultiplexer.Connect(redisConfig);
            services.AddSingleton<IConnectionMultiplexer>(sharedMultiplexer);

            services.AddStackExchangeRedisCache(options =>
            {
                options.ConnectionMultiplexerFactory = () =>
                    Task.FromResult<IConnectionMultiplexer>(sharedMultiplexer);
            });

            // Persist Data Protection keys (auth cookies, reset/confirmation tokens, antiforgery) to
            // Redis so multi-instance hosts share a key ring and tokens survive rolling restarts.
            services.AddDataProtection()
                .PersistKeysToStackExchangeRedis(sharedMultiplexer, "DataProtection-Keys")
                .SetApplicationName("Boilerplate");
        }

        // HybridCache auto-composes with whatever IDistributedCache is registered above.
        services.AddHybridCache(options =>
        {
            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = cacheOptions.DefaultExpiration,            // L1 + L2 total lifetime
                LocalCacheExpiration = cacheOptions.DefaultLocalCacheExpiration, // L1 only
            };
            options.MaximumKeyLength = cacheOptions.MaximumKeyLength;
            options.MaximumPayloadBytes = cacheOptions.MaximumPayloadBytes;
        });

        // The tenancy seam. The block may not reference the Multitenancy module (architecture test),
        // so it declares what it needs and the module supplies it. A host without multitenancy says so
        // here rather than being quietly handed an untenanted key space.
        if (singleTenant)
        {
            services.TryAddSingleton<ICacheTenantAccessor, SingleTenantCacheTenantAccessor>();
        }

        services.TryAddSingleton(sp => new CacheKeyScope(
            sp.GetService<ICacheTenantAccessor>()
                ?? throw new InvalidOperationException(MissingTenantAccessorMessage)));

        // Wrap HybridCache twice: capture the descriptor AddHybridCache installed, remove it, and
        // register factories that build the inner cache once and layer the decorators over it.
        DecorateHybridCache(services);

        return services;
    }

    /// <summary>
    /// Replaces the raw <see cref="HybridCache"/> registration with the decorator stack. Order is
    /// inner → outer: telemetry sits closest to the real cache so it records the physical key that
    /// was actually written, and tenant scoping sits outermost so a missing tenant throws before any
    /// span opens or any counter moves. Both public caches share the one telemetry-wrapped instance,
    /// so the tenant and global key spaces live in the same L1/L2.
    /// </summary>
    private static void DecorateHybridCache(IServiceCollection services)
    {
        var originalDescriptor = services.LastOrDefault(d => d.ServiceType == typeof(HybridCache))
            ?? throw new InvalidOperationException("HybridCache is not registered. AddHybridCache must be called before DecorateHybridCache.");

        services.Remove(originalDescriptor);

        services.AddSingleton(sp =>
        {
            HybridCache inner;
            if (originalDescriptor.ImplementationInstance is HybridCache instance)
            {
                inner = instance;
            }
            else if (originalDescriptor.ImplementationFactory is { } factory)
            {
                inner = (HybridCache)factory(sp);
            }
            else
            {
                inner = (HybridCache)ActivatorUtilities.CreateInstance(sp, originalDescriptor.ImplementationType!);
            }

            return new ObservableHybridCache(inner);
        });

        services.AddSingleton<HybridCache>(sp => new TenantScopedHybridCache(
            sp.GetRequiredService<ObservableHybridCache>(),
            sp.GetRequiredService<CacheKeyScope>()));

        services.AddSingleton(sp => new GlobalHybridCache(
            sp.GetRequiredService<ObservableHybridCache>()));
    }
}
