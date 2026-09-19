using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Boilerplate.BuildingBlocks.Storage.Keys;
using Boilerplate.BuildingBlocks.Storage.Local;
using Boilerplate.BuildingBlocks.Storage.S3;
using Boilerplate.BuildingBlocks.Storage.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Boilerplate.BuildingBlocks.Storage;

public static class Extensions
{
    public static IServiceCollection AddHeroLocalFileStorage(this IServiceCollection services)
    {
        AddTenantStorageKeys(services);
        services.AddScoped<IStorageService, LocalStorageService>();
        return services;
    }

    public static IServiceCollection AddHeroStorage(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        AddTenantStorageKeys(services);

        var provider = configuration["Storage:Provider"]?.ToLowerInvariant();

        if (string.Equals(provider, "s3", StringComparison.OrdinalIgnoreCase))
        {
            services.Configure<S3StorageOptions>(configuration.GetSection("Storage:S3"));

            services.AddSingleton<IAmazonS3>(sp =>
            {
                var options = sp.GetRequiredService<IOptions<S3StorageOptions>>().Value;

                if (string.IsNullOrWhiteSpace(options.Bucket))
                {
                    throw new InvalidOperationException("Storage:S3:Bucket is required when using S3 storage.");
                }

                var config = new AmazonS3Config();

                if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
                {
                    // S3-compatible endpoint (e.g. MinIO). Path-style addressing is typically required
                    // because these services don't route virtual-hosted-style bucket subdomains.
                    config.ServiceURL = options.ServiceUrl;
                    config.ForcePathStyle = options.ForcePathStyle;

                    // The SDK still wants an auth region for SigV4 even when hitting a custom endpoint.
                    config.AuthenticationRegion = string.IsNullOrWhiteSpace(options.Region) ? "us-east-1" : options.Region;
                }
                else if (!string.IsNullOrWhiteSpace(options.Region))
                {
                    config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
                }

                var hasExplicitCredentials = !string.IsNullOrWhiteSpace(options.AccessKey)
                    && !string.IsNullOrWhiteSpace(options.SecretKey);

                return hasExplicitCredentials
                    ? new AmazonS3Client(new BasicAWSCredentials(options.AccessKey, options.SecretKey), config)
                    : new AmazonS3Client(config);
            });

            services.AddTransient<S3StorageService>();
            RegisterStorageService<S3StorageService>(services, ServiceLifetime.Transient);
        }
        else
        {
            services.AddScoped<LocalStorageService>();
            RegisterStorageService<LocalStorageService>(services, ServiceLifetime.Scoped);
        }

        return services;
    }

    /// <summary>
    /// Singleton, like the Finbuckle accessor it wraps (which is itself backed by an
    /// <c>AsyncLocal</c>): the ambient tenant is read on every call, never captured, so one
    /// instance is correct inside a request, inside a job's tenant scope, and across the tenant
    /// switches a per-tenant fan-out makes.
    /// </summary>
    private static void AddTenantStorageKeys(IServiceCollection services) =>
        services.TryAddSingleton<ITenantStorageKeys, TenantStorageKeys>();

    private static void RegisterStorageService<TInner>(
        IServiceCollection services,
        ServiceLifetime innerLifetime)
        where TInner : class, IStorageService
    {
        services.Add(new ServiceDescriptor(
            typeof(IStorageService),
            sp => sp.GetRequiredService<TInner>(),
            innerLifetime));
    }
}
