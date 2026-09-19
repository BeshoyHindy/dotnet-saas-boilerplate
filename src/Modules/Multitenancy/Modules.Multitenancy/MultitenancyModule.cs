using Asp.Versioning;
using Finbuckle.MultiTenant;
using Finbuckle.MultiTenant.Abstractions;
using Finbuckle.MultiTenant.AspNetCore.Extensions;
using Finbuckle.MultiTenant.EntityFrameworkCore.Stores;
using Finbuckle.MultiTenant.Extensions;
using Finbuckle.MultiTenant.Stores;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Eventing.Abstractions;
using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.BuildingBlocks.Web.Health;
using Boilerplate.BuildingBlocks.Web.Modules;
using Boilerplate.Modules.Multitenancy.Contracts;
using Boilerplate.Modules.Multitenancy.Data;
using Boilerplate.Modules.Multitenancy.Features.v1.AdjustTenantValidity;
using Boilerplate.Modules.Multitenancy.Features.v1.ChangeTenantActivation;
using Boilerplate.Modules.Multitenancy.Features.v1.CreateTenant;
using Boilerplate.Modules.Multitenancy.Features.v1.GetMyTenantStatus;
using Boilerplate.Modules.Multitenancy.Features.v1.GetTenantMigrations;
using Boilerplate.Modules.Multitenancy.Features.v1.GetTenants;
using Boilerplate.Modules.Multitenancy.Features.v1.GetTenantStatus;
using Boilerplate.Modules.Multitenancy.Features.v1.GetTenantTheme;
using Boilerplate.Modules.Multitenancy.Features.v1.ResetTenantTheme;
using Boilerplate.Modules.Multitenancy.Features.v1.TenantProvisioning.GetTenantProvisioningStatus;
using Boilerplate.Modules.Multitenancy.Features.v1.TenantProvisioning.RetryTenantProvisioning;
using Boilerplate.Modules.Multitenancy.Features.v1.RenewTenant;
using Boilerplate.Modules.Multitenancy.Features.v1.UpdateTenantTheme;
using Boilerplate.Modules.Multitenancy.Provisioning;
using Boilerplate.Modules.Multitenancy.Resolution;
using Boilerplate.Modules.Multitenancy.Services;
using Hangfire;
using Hangfire.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace Boilerplate.Modules.Multitenancy;

public sealed class MultitenancyModule : IModule
{
    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddPermissions(
            Boilerplate.Modules.Multitenancy.Contracts.Authorization.MultitenancyPermissions.All);

        builder.Services.Configure<TenantValidityOptions>(
            builder.Configuration.GetSection(TenantValidityOptions.SectionName));

        builder.Services.AddScoped<ITenantService, TenantService>();
        builder.Services.AddScoped<ITenantThemeService, TenantThemeService>();
        builder.Services.AddTransient<IConnectionStringValidator, ConnectionStringValidator>();
        builder.Services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();
        builder.Services.AddTransient<TenantProvisioningJob>();
        builder.Services.AddTransient<TenantExpiryScanJob>();

        // Singleton — the buffer survives the request scope that calls Store(...)
        // so the background Hangfire-scheduled seed scope can still TryConsume(...).
        builder.Services.AddSingleton<
            Boilerplate.BuildingBlocks.Shared.Multitenancy.ITenantInitialPasswordBuffer,
            Services.TenantInitialPasswordBuffer>();

        builder.Services.AddHeroDbContext<TenantDbContext>();

        // The one way to enter a tenant outside a request (ADR-0002, "Jobs and events"). Singletons:
        // both are stateless and reach the scoped tenant store through IServiceScopeFactory. Registered
        // here rather than in BuildingBlocks because they need Finbuckle's store, which this module owns.
        builder.Services.AddSingleton<AmbientTenantContext>();
        builder.Services.AddSingleton<ITenantScope, TenantScope>();

        // Replace (not Add) the no-op event tenant scope with a Finbuckle-backed one so background
        // event dispatch establishes the tenant before tenant-filtered handler DbContexts are built.
        builder.Services.Replace(
            ServiceDescriptor.Singleton<IEventTenantScope, FinbuckleEventTenantScope>());

        // Same idea one level up: the outbox dispatcher must visit every database that can hold
        // outbox rows. Without these, a tenant with a dedicated connection string writes rows the
        // dispatcher never polls. Scoped provider — IMultiTenantStore is scoped.
        builder.Services.Replace(
            ServiceDescriptor.Singleton<IEventingDrainScope, FinbuckleEventingDrainScope>());
        builder.Services.Replace(
            ServiceDescriptor.Scoped<IEventingDrainTargetProvider, TenantStoreDrainTargetProvider>());

        builder.Services
            .AddMultiTenant<AppTenantInfo>(options =>
            {
                options.Events.OnTenantResolveCompleted = async context =>
                {
                    if (context.MultiTenantContext.StoreInfo is null) return;
                    if (context.MultiTenantContext.StoreInfo.StoreType != typeof(DistributedCacheStore<AppTenantInfo>))
                    {
                        var sp = ((HttpContext)context.Context!).RequestServices;
                        var distributedStore = sp
                            .GetRequiredService<IEnumerable<IMultiTenantStore<AppTenantInfo>>>()
                            .FirstOrDefault(s => s.GetType() == typeof(DistributedCacheStore<AppTenantInfo>));

                        await distributedStore!.AddAsync(context.MultiTenantContext.TenantInfo!);
                    }
                    await Task.CompletedTask;
                };
            })
            // ── One strategy, no chain (ADR-0002) ─────────────────────────────
            // Authenticated → the token's `tenant` claim; anonymous → the {tenant} route value on the
            // endpoints marked [TenantFromRoute]. The caller can never name a tenant on the wire, so
            // there is no header/query/host input left to compensate for.
            .WithStrategy<TokenOrRouteTenantStrategy>(ServiceLifetime.Singleton)
            .WithDistributedCacheStore(TimeSpan.FromMinutes(60))
            .WithStore<EFCoreStore<TenantDbContext, AppTenantInfo>>(ServiceLifetime.Scoped);

        builder.Services.AddHealthChecks()
            // The only per-module database check tagged for readiness: every module's DbContext
            // talks to the same PostgreSQL server, and no request can be served without the tenant
            // catalog, so this one check answers the readiness question for all of them.
            .AddDbContextCheck<TenantDbContext>(
                name: "db:multitenancy",
                failureStatus: HealthStatus.Unhealthy,
                tags: [HealthTags.Ready])
            .AddCheck<TenantMigrationsHealthCheck>(
                name: "db:tenants-migrations",
                failureStatus: HealthStatus.Unhealthy);
    }

    public void ConfigureMiddleware(IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // ── Tenant resolution ──────────────────────────────────────────
        // Deliberately here and not in the host: module middleware runs inside UseModuleMiddlewares(),
        // i.e. after UseRouting() and UseAuthentication(). Resolution therefore sees the authenticated
        // principal (for the `tenant` claim) and the matched endpoint (for the anonymous auth routes).
        // Multitenancy's AppModule order (200) puts this ahead of every module that needs the tenant.
        app.UseMultiTenant();

        // ── Token-without-tenant guard ─────────────────────────────────
        // One token, one tenant (ADR-0002). A validly signed token whose `tenant` claim is missing,
        // blank, or names a tenant the store no longer knows must not proceed with an ambient tenant
        // of "none" — that would silently run tenant-filtered queries against nothing. 401, same as
        // any other unusable credential.
        app.Use(async (ctx, next) =>
        {
            if (ctx.User?.Identity?.IsAuthenticated == true &&
                ctx.RequestServices.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>()
                    .MultiTenantContext?.TenantInfo is null)
            {
                throw new UnauthorizedException("The access token does not identify a valid tenant.");
            }

            await next(ctx).ConfigureAwait(false);
        });

        // ── Deactivated-tenant guard ───────────────────────────────────
        // Finbuckle resolves inactive tenants normally, so this guard rejects any request (incl. the
        // anonymous login/refresh routes) with a non-root inactive tenant; root operators are exempt.
        app.Use(async (ctx, next) =>
        {
            var accessor = ctx.RequestServices.GetRequiredService<IMultiTenantContextAccessor<AppTenantInfo>>();
            var tenant = accessor.MultiTenantContext?.TenantInfo;

            // The resolved tenant is the only input now — no claim fallback is needed, because
            // resolution already ran post-authentication and used the claim itself.
            if (tenant is not null &&
                !string.Equals(tenant.Id, MultitenancyConstants.Root.Id, StringComparison.Ordinal))
            {
                if (!tenant.IsActive)
                {
                    throw new ForbiddenException("This tenant has been deactivated. Contact your administrator.");
                }

                // Expiry is enforced on every request (not just at login) with a grace period:
                // a tenant past ValidUpto still works until ValidUpto + grace, then is hard-blocked.
                var graceDays = ctx.RequestServices
                    .GetRequiredService<IOptions<TenantValidityOptions>>().Value.GracePeriodDays;
                var nowUtc = ctx.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime;
                var graceEndsUtc = tenant.ValidUpto.AddDays(graceDays);
                if (nowUtc > graceEndsUtc)
                {
                    throw new ForbiddenException("This tenant's subscription has expired. Please renew to continue.");
                }

                // Inside the grace period: surface days-left so clients can warn. Set via OnStarting so
                // the header survives even when an exception handler rewrites the response.
                if (nowUtc > tenant.ValidUpto)
                {
                    var daysLeft = (int)Math.Ceiling((graceEndsUtc - nowUtc).TotalDays);
                    var headerValue = daysLeft.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    ctx.Response.OnStarting(static state =>
                    {
                        var (response, value) = ((HttpResponse, string))state;
                        response.Headers["X-Subscription-Grace"] = value;
                        return Task.CompletedTask;
                    }, (ctx.Response, headerValue));
                }
            }

            await next(ctx).ConfigureAwait(false);
        });
    }

    public void MapEndpoints(IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var versionSet = endpoints.NewApiVersionSet()
            .HasApiVersion(new ApiVersion(1))
            .ReportApiVersions()
            .Build();

        var group = endpoints.MapGroup("api/v{version:apiVersion}/tenants")
            .WithTags("Tenants")
            .WithApiVersionSet(versionSet);
        ChangeTenantActivationEndpoint.Map(group);
        GetTenantsEndpoint.Map(group);
        RenewTenantEndpoint.Map(group);
        AdjustTenantValidityEndpoint.Map(group);
        CreateTenantEndpoint.Map(group);
        GetTenantStatusEndpoint.Map(group);
        GetMyTenantStatusEndpoint.Map(group);
        GetTenantProvisioningStatusEndpoint.Map(group);
        RetryTenantProvisioningEndpoint.Map(group);
        TenantMigrationsEndpoint.Map(group);

        // Theme endpoints
        GetTenantThemeEndpoint.Map(group);
        UpdateTenantThemeEndpoint.Map(group);
        ResetTenantThemeEndpoint.Map(group);

        var jobManager = endpoints.ServiceProvider.GetService<IRecurringJobManager>();
        if (jobManager is not null)
        {
            // Scan tenants daily at 02:00 UTC; publishes nearing-expiry / entered-grace / expired notices.
            jobManager.AddOrUpdate(
                "tenant-expiry-scan",
                Job.FromExpression<TenantExpiryScanJob>(j => j.RunAsync(CancellationToken.None)),
                "0 2 * * *",
                new RecurringJobOptions { TimeZone = TimeZoneInfo.Utc });
        }
    }
}