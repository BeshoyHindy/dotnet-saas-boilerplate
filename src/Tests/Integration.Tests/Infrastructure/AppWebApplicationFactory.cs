using System.Reflection;
using Amazon.S3;
using Amazon.S3.Model;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Jobs;
using Boilerplate.BuildingBlocks.Jobs.Services;
using Boilerplate.BuildingBlocks.Mailing;
using Boilerplate.BuildingBlocks.Mailing.Services;
using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Microsoft.AspNetCore.WebUtilities;
using System.Text;
using Boilerplate.Modules.Multitenancy.Data;
using Boilerplate.BuildingBlocks.Web.Modules;
using Hangfire;
using Hangfire.InMemory;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Testcontainers.Minio;
using Testcontainers.PostgreSql;

namespace Integration.Tests.Infrastructure;

public sealed class AppWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private const string MinioImage = "quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z";
    private const string MinioAccessKey = "minioadmin";
    private const string MinioSecretKey = "minioadmin";
    private const string MinioBucket = "boilerplate-integration-test-uploads";

    private static readonly SemaphoreSlim _migrationLock = new(1, 1);
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("boilerplate_integration_tests")
        .WithUsername("postgres")
        .WithPassword("integration_test_pwd")
        .WithAutoRemove(true)
        .WithCleanUp(true)
        .Build();

    // MinIO no longer publishes to Docker Hub, so `minio/minio:*` fails to pull on any machine
    // without a cached layer. Pull from quay.io, pinned to the same release as docker-compose.yml.
    private readonly MinioContainer _minio = new MinioBuilder(MinioImage)
        .WithUsername(MinioAccessKey)
        .WithPassword(MinioSecretKey)
        .WithAutoRemove(true)
        .WithCleanUp(true)
        .Build();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _minio.StartAsync());
        await CreateMinioBucketAsync();

        // Force host creation via the Server property (no leaked HttpClient)
        _ = Server;

        // Migrate + seed the root tenant. The semaphore stops test classes sharing the DB
        // from migrating simultaneously.
        await _migrationLock.WaitAsync();
        try
        {
            await ProvisionRootTenantAsync();
        }
        finally
        {
            _migrationLock.Release();
        }
    }

    public new async Task DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
        await _minio.DisposeAsync();
    }

    /// <summary>The MinIO endpoint URL exposed to the host configuration; useful for tests that need to PUT bytes directly.</summary>
    public string MinioServiceUrl => _minio.GetConnectionString();

    private async Task CreateMinioBucketAsync()
    {
        var config = new AmazonS3Config
        {
            ServiceURL = _minio.GetConnectionString(),
            ForcePathStyle = true,
            UseHttp = true,
            AuthenticationRegion = "us-east-1"
        };

        using var client = new AmazonS3Client(
            new Amazon.Runtime.BasicAWSCredentials(MinioAccessKey, MinioSecretKey),
            config);

        try
        {
            await client.PutBucketAsync(new PutBucketRequest { BucketName = MinioBucket });
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode == "BucketAlreadyOwnedByYou" || ex.ErrorCode == "BucketAlreadyExists")
        {
            // Idempotent across factory re-creations.
        }

        // The same grant the deploy stacks apply (`mc anonymous set download …/uploads`, pinned by
        // deploy/dokploy/tests/compose-contract.test.sh): anonymous GET on the `uploads/` prefix and
        // nowhere else. Without it the durable avatar and brand-asset URLs the API hands back would
        // 403 here, and a test could not tell a working link from a broken one. The `tenants/`
        // prefix staying closed is the other half — it is what the Files public-URL tests lean on.
        await client.PutBucketPolicyAsync(new PutBucketPolicyRequest
        {
            BucketName = MinioBucket,
            Policy = $$"""
                {
                  "Version": "2012-10-17",
                  "Statement": [
                    {
                      "Effect": "Allow",
                      "Principal": { "AWS": ["*"] },
                      "Action": ["s3:GetObject"],
                      "Resource": ["arn:aws:s3:::{{MinioBucket}}/uploads/*"]
                    }
                  ]
                }
                """
        });
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DatabaseOptions:Provider"] = "POSTGRESQL",
                ["DatabaseOptions:ConnectionString"] = _postgres.GetConnectionString(),
                ["DatabaseOptions:MigrationsAssembly"] = "Boilerplate.Migrations.PostgreSQL",
                ["CachingOptions:Redis"] = "",
                ["JwtOptions:Issuer"] = TestConstants.JwtIssuer,
                ["JwtOptions:Audience"] = TestConstants.JwtAudience,
                ["JwtOptions:SigningKey"] = TestConstants.JwtSigningKey,
                ["JwtOptions:AccessTokenMinutes"] = "30",
                ["JwtOptions:RefreshTokenDays"] = "7",
                // Acting-token ceiling (operator exchange + impersonation). Kept below the shipped
                // default so a test can prove an over-long request is clamped, not honoured.
                ["OperatorExchange:DefaultMinutes"] = "15",
                ["OperatorExchange:MaxMinutes"] =
                    TestConstants.OperatorExchangeMaxMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["OriginOptions:OriginUrl"] = "http://localhost",
                ["OpenTelemetryOptions:Enabled"] = "false",
                ["EventingOptions:UseHostedServiceDispatcher"] = "false",
                ["Serilog:MinimumLevel:Default"] = "Warning",
                ["Serilog:MinimumLevel:Override:Microsoft.EntityFrameworkCore"] = "Fatal",
                ["Serilog:MinimumLevel:Override:Npgsql"] = "Fatal",
                ["Serilog:WriteTo:0:Name"] = "Console",
                ["Serilog:WriteTo:0:Args:restrictedToMinimumLevel"] = "Warning",
                ["Serilog:WriteTo:1:Name"] = "",
                ["MailOptions:UseSendGrid"] = "false",
                ["HangfireOptions:Route"] = "/jobs",
                ["RateLimitingOptions:Enabled"] = "false",
                ["PasswordPolicy:EnforcePasswordExpiry"] = "false",
                ["Seed:DefaultAdminPassword"] = TestConstants.DefaultPassword,
                // Read by the migrator's demo seeder (DemoSeedTests drives it against this host).
                ["Seed:DemoPassword"] = TestConstants.DemoPassword,
                ["SecurityHeadersOptions:Enabled"] = "false",
                ["Storage:Provider"] = "s3",
                ["Storage:S3:Bucket"] = MinioBucket,
                ["Storage:S3:ServiceUrl"] = _minio.GetConnectionString(),
                ["Storage:S3:AccessKey"] = MinioAccessKey,
                ["Storage:S3:SecretKey"] = MinioSecretKey,
                ["Storage:S3:ForcePathStyle"] = "true",
                ["Storage:S3:PublicRead"] = "false",
                ["Storage:S3:Region"] = "us-east-1",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Remove hosted services that need unavailable infra or race migrations (RolePermissionSync,
            // Hangfire server + stale-lock cleanup, OutboxDispatcher); we register our own InMemory server below.
            var hostedServicesToRemove = services
                .Where(d => d.ServiceType == typeof(IHostedService) &&
                    (d.ImplementationType?.Name == "RolePermissionSyncHostedService" ||
                     d.ImplementationType?.FullName?.Contains("Hangfire", StringComparison.Ordinal) == true ||
                     d.ImplementationType?.Name == "HangfireStaleLockCleanupService" ||
                     d.ImplementationType?.Name == "OutboxDispatcherHostedService"))
                .ToList();
            foreach (var service in hostedServicesToRemove)
            {
                services.Remove(service);
            }

            // Storage is swapped for the in-memory one; the ACTIVATOR AND FILTERS STAY THE PRODUCTION
            // ONES. AddHangfire registers IGlobalConfiguration with AddSingleton, so this later call
            // wins outright — without re-applying the pipeline the suite would exercise a job runtime
            // with no tenant stamping and no tenant scope, i.e. not the one that ships.
            services.AddHangfire((provider, config) =>
            {
                config.UseInMemoryStorage();
                config.UseHeroJobPipeline(provider);
            });
            services.AddHangfireServer(options =>
            {
                options.SchedulePollingInterval = TimeSpan.FromSeconds(1);
                options.Queues = ["default", "email"];
                options.WorkerCount = 2;
            });
            services.TryAddTransient<IJobService, HangfireService>();

            services.PostConfigure<JwtBearerOptions>(
                JwtBearerDefaults.AuthenticationScheme,
                options => options.RequireHttpsMetadata = false);

            // Fault-injection seam for the provisioning failure/retry suites. Appended to the
            // IDbInitializer enumerable ITenantService.MigrateTenantAsync iterates, so an armed
            // tenant fails the real Migrations step; every other tenant sees a no-op.
            services.AddScoped<IDbInitializer, FaultInjectingDbInitializer>();

            // Fault-injection seam for the registration-atomicity suite (#86): the real store, wrapped
            // so an armed registration's publish writes its row and then throws. IOutboxWriter is
            // registered as a forward to IOutboxStore, so decorating the one covers both, and every
            // unarmed publish — including the dispatcher's own reads and marks — is the production path.
            // Installed suite-wide but inert everywhere else: only a registration whose address was
            // explicitly armed throws, and Arm refuses any address not starting with
            // FaultInjectingOutboxStore.EmailPrefix, which no other test uses.
            services.RemoveAll<Boilerplate.BuildingBlocks.Eventing.Outbox.IOutboxStore>();
            services.AddScoped<Boilerplate.BuildingBlocks.Eventing.Outbox.EfCoreOutboxStore>();
            services.AddScoped<Boilerplate.BuildingBlocks.Eventing.Outbox.IOutboxStore, FaultInjectingOutboxStore>();

            // Probe handler for the tenant-context tests: registered here so it is dispatched by the
            // real bus, through the real IEventTenantScope, with a real inbox — the whole point is
            // that nothing in that path is substituted.
            services.AddScoped<
                Boilerplate.BuildingBlocks.Eventing.Abstractions.IIntegrationEventHandler<
                    Tests.Jobs.TenantProbeIntegrationEvent>,
                Tests.Jobs.TenantProbeHandler>();

            // Replace real mail service with a no-op to avoid SMTP errors and Hangfire retries.
            // Register the concrete type as well and alias the interface to it: Hangfire records the
            // *concrete* type in the serialized job, and AppJobActivator resolves it with
            // GetServiceOrCreateInstance — without the concrete registration every mail job would run
            // against a throwaway instance and its MailRequest would never reach `Sent`.
            services.RemoveAll<IMailService>();
            services.AddSingleton<NoOpMailService>();
            services.AddSingleton<IMailService>(sp => sp.GetRequiredService<NoOpMailService>());

            // Detailed errors in tests instead of generic "An unexpected error occurred"
            var existingHandlers = services.Where(d =>
                d.ServiceType == typeof(Microsoft.AspNetCore.Diagnostics.IExceptionHandler)).ToList();
            foreach (var h in existingHandlers) services.Remove(h);
            services.AddExceptionHandler<DetailedTestExceptionHandler>();

            // AddHeroStorage reads `Storage:Provider` eagerly (before the test config overlay applies), so it
            // wires LocalStorageService. Replace it here with the S3 stack pointed at the MinIO testcontainer.
            RewireStorageForS3(services);
        });
    }

    private void RewireStorageForS3(IServiceCollection services)
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(Boilerplate.BuildingBlocks.Storage.Services.IStorageService)
                     || d.ServiceType == typeof(Boilerplate.BuildingBlocks.Storage.Local.LocalStorageService)
                     || d.ServiceType == typeof(Boilerplate.BuildingBlocks.Storage.S3.S3StorageService)
                     || d.ServiceType == typeof(IAmazonS3))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);

        services.Configure<Boilerplate.BuildingBlocks.Storage.S3.S3StorageOptions>(opts =>
        {
            opts.Bucket = MinioBucket;
            opts.ServiceUrl = _minio.GetConnectionString();
            opts.AccessKey = MinioAccessKey;
            opts.SecretKey = MinioSecretKey;
            opts.ForcePathStyle = true;
            opts.PublicRead = false;
            opts.Region = "us-east-1";
        });

        services.AddSingleton<IAmazonS3>(_ =>
        {
            var config = new AmazonS3Config
            {
                ServiceURL = _minio.GetConnectionString(),
                ForcePathStyle = true,
                UseHttp = true,
                AuthenticationRegion = "us-east-1"
            };
            return new AmazonS3Client(
                new Amazon.Runtime.BasicAWSCredentials(MinioAccessKey, MinioSecretKey),
                config);
        });
        services.AddTransient<Boilerplate.BuildingBlocks.Storage.S3.S3StorageService>();
        services.AddTransient<Boilerplate.BuildingBlocks.Storage.Services.IStorageService>(sp =>
            sp.GetRequiredService<Boilerplate.BuildingBlocks.Storage.S3.S3StorageService>());
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Disable ValidateOnBuild for .NET 10 minimal API dual-host model
        builder.UseServiceProviderFactory(new DefaultServiceProviderFactory(new ServiceProviderOptions
        {
            ValidateOnBuild = false,
            ValidateScopes = false
        }));

        ResetModuleLoader();
        return base.CreateHost(builder);
    }

    private async Task ProvisionRootTenantAsync()
    {
        // 1. Explicitly migrate the tenant catalog FIRST.
        using (var scope = Services.CreateScope())
        {
            var tenantDbContext = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
            await tenantDbContext.Database.MigrateAsync();

            // 2. Seed Root Tenant if missing (ensures we don't wait for background service)
            var rootTenant = await tenantDbContext.TenantInfo.FindAsync(MultitenancyConstants.Root.Id);
            if (rootTenant is null)
            {
                rootTenant = new AppTenantInfo(
                    MultitenancyConstants.Root.Id,
                    MultitenancyConstants.Root.Id,
                    MultitenancyConstants.Root.Name)
                {
                    AdminEmail = MultitenancyConstants.Root.EmailAddress,
                    IsActive = true,
                    Issuer = MultitenancyConstants.Root.Issuer,
                };

                var validUpto = DateTime.UtcNow.AddYears(1);
                rootTenant.SetValidity(validUpto);
                await tenantDbContext.TenantInfo.AddAsync(rootTenant);
                await tenantDbContext.SaveChangesAsync();
            }

            // 3. Run all module migrations (identity, audit, files schemas)
            var setter = scope.ServiceProvider.GetRequiredService<IMultiTenantContextSetter>();
            setter.MultiTenantContext = new MultiTenantContext<AppTenantInfo>(rootTenant);

            foreach (var init in scope.ServiceProvider.GetServices<IDbInitializer>())
            {
                await init.MigrateAsync(CancellationToken.None);
            }

            // 4. Seed all modules (admin user, roles, permissions, groups)
            foreach (var init in scope.ServiceProvider.GetServices<IDbInitializer>())
            {
                await init.SeedAsync(CancellationToken.None);
            }

            // 5. Run the role-permission syncer through the production code path.
            var syncer = scope.ServiceProvider.GetRequiredService<Boilerplate.Modules.Identity.Authorization.RolePermissionSyncer>();
            await syncer.SyncAsync(CancellationToken.None);
        }
    }

    private static void ResetModuleLoader()
    {
        var type = typeof(ModuleLoader);
        var modulesField = type.GetField("_modules", BindingFlags.Static | BindingFlags.NonPublic);
        var loadedField = type.GetField("_modulesLoaded", BindingFlags.Static | BindingFlags.NonPublic);

        if (modulesField?.GetValue(null) is System.Collections.IList list)
        {
            list.Clear();
        }

        loadedField?.SetValue(null, false);
    }
}
