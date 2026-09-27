using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Jobs.Services;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.BuildingBlocks.Shared.Identity.Claims;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.BuildingBlocks.Shared.Persistence;
using Hangfire;
using Hangfire.Dashboard;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Boilerplate.BuildingBlocks.Jobs;

public static class Extensions
{
    public static IServiceCollection AddAppJobs(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<HangfireOptions>()
            .BindConfiguration(nameof(HangfireOptions))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHangfireServer(options =>
        {
            options.HeartbeatInterval = TimeSpan.FromSeconds(30);
            options.Queues = ["default", "email"];
            options.WorkerCount = 5;
            options.SchedulePollingInterval = TimeSpan.FromSeconds(30);
        });

        services.AddHangfire((provider, config) =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var dbOptions = configuration.GetSection(nameof(DatabaseOptions)).Get<DatabaseOptions>()
                ?? throw new CustomException("Database options not found");

            if (!string.Equals(dbOptions.Provider, DbProviders.PostgreSQL, StringComparison.OrdinalIgnoreCase))
            {
                throw new CustomException(
                    $"Hangfire storage provider {dbOptions.Provider} is not supported. Only {DbProviders.PostgreSQL} is supported.");
            }

            config.UsePostgreSqlStorage(o =>
            {
                o.UseNpgsqlConnection(dbOptions.ConnectionString);
            });

            config.UseAppJobPipeline(provider);
        });

        // Deferred stale lock cleanup — runs after app starts accepting requests
        services.AddHostedService<HangfireStaleLockCleanupService>();

        services.AddTransient<IJobService, HangfireService>();

        // Hangfire validates antiforgery on the Job monitor's POSTs (retry, delete, trigger) whenever
        // an IAntiforgery is registered, and embeds the request token in every page it renders. The
        // Job monitor is reachable from a browser through a cookie (ADR-0009), so a cross-site form
        // must not be able to drive those writes: SameSite=Strict on that cookie is the first line,
        // this is the second. The API binds no forms anywhere else, so no other endpoint is affected.
        // The token cookie is scoped to the Job monitor route, like the credential it protects.
        services.AddAntiforgery();
        services.AddOptions<AntiforgeryOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                options.Cookie.Name = JobMonitor.AntiforgeryCookieName;
                options.Cookie.Path = JobMonitor.RouteFrom(configuration);
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.HttpOnly = true;
            });

        return services;
    }


    /// <summary>
    /// Activator + filters, in one place: the tenant/user stamping on enqueue
    /// (<see cref="AppJobFilter"/>) and the tenant scope on execution (<see cref="AppJobActivator"/>)
    /// are what make ADR-0002 hold for background work. Exposed so a host that swaps Hangfire's
    /// storage — the integration-test host runs it in memory — reconfigures storage <i>only</i>,
    /// instead of quietly losing the pipeline and testing a job runtime nobody ships.
    /// </summary>
    public static IGlobalConfiguration UseAppJobPipeline(this IGlobalConfiguration config, IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(provider);

        config.UseActivator(new AppJobActivator(provider));
        config.UseFilter(new AppJobFilter(provider));
        config.UseFilter(new LogJobFilter());
        config.UseFilter(new HangfireTelemetryFilter());

        return config;
    }

    /// <summary>
    /// Mounts the Job monitor (Hangfire's dashboard) as a routed endpoint gated by
    /// <see cref="SystemPermissions.Hangfire.View"/>. Must be called after
    /// <c>UseAuthentication()</c>/<c>UseAuthorization()</c> so the platform's own authentication and
    /// permission policy are the gate — there is no separate dashboard credential.
    /// </summary>
    /// <remarks>
    /// The endpoint carries <see cref="JobMonitorEndpointMetadata"/>, which is what lets the Job
    /// monitor cookie scheme authenticate a request here and nowhere else (ADR-0009). Without
    /// <see cref="SystemPermissions.Hangfire.Manage"/> the Job monitor is read-only.
    /// </remarks>
    public static IEndpointRouteBuilder MapAppJobDashboard(this IEndpointRouteBuilder endpoints, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(config);

        var dashboardOptions = new DashboardOptions
        {
            AppPath = "/",

            // No gate here. Hangfire's default filter chain is LocalRequestsOnly, which would reject
            // every remote operator the permission policy just allowed; ASP.NET Core authorization is
            // the single gate.
            Authorization = [],

            // Not a gate either — it always answers yes. It works out, asynchronously and once per
            // request, whether the caller may write; IsReadOnlyFunc is synchronous and cannot.
            AsyncAuthorization = [new JobMonitorWriteAccess()],
            IsReadOnlyFunc = JobMonitorWriteAccess.IsReadOnly,
        };

        endpoints.MapHangfireDashboard(JobMonitor.RouteFrom(config), dashboardOptions)
            .RequirePermission(SystemPermissions.Hangfire.View)
            .WithMetadata(JobMonitorEndpointMetadata.Instance)
            .ExemptFromTenantSweep(
                "{**path} is Hangfire's own page/asset routing, not a tenant resource id. The route is " +
                "root-only (Permissions.Hangfire.View is IsRoot) and the jobs it shows carry the tenant " +
                "captured at enqueue time, never one named in this URL.");

        return endpoints;
    }

    /// <summary>
    /// Decides once per Job monitor request whether the caller holds
    /// <see cref="SystemPermissions.Hangfire.Manage"/> and records it for
    /// <see cref="DashboardOptions.IsReadOnlyFunc"/>. Unknown means read-only: a request the check
    /// could not answer for gets neither the write buttons nor the write routes.
    /// </summary>
    private sealed class JobMonitorWriteAccess : IDashboardAsyncAuthorizationFilter
    {
        private static readonly object ItemKey = new();

        public async Task<bool> AuthorizeAsync(DashboardContext context)
        {
            var httpContext = context.GetHttpContext();
            var userId = httpContext.User.GetUserId();
            var checker = httpContext.RequestServices.GetService<IPermissionChecker>();

            var canManage = userId is not null
                && checker is not null
                && await checker
                    .HasPermissionAsync(userId, SystemPermissions.Hangfire.Manage, httpContext.RequestAborted)
                    .ConfigureAwait(false);

            httpContext.Items[ItemKey] = canManage;
            return true;
        }

        public static bool IsReadOnly(DashboardContext context) =>
            context.GetHttpContext().Items[ItemKey] is not true;
    }
}

/// <summary>
/// The Job monitor: Hangfire's dashboard, mounted at <see cref="HangfireOptions.Route"/> (ADR-0009).
/// </summary>
public static class JobMonitor
{
    /// <summary>
    /// The authentication scheme that reads the Job monitor cookie. It is accepted only on the
    /// endpoint carrying <see cref="JobMonitorEndpointMetadata"/>; every other endpoint is bearer-only.
    /// </summary>
    public const string CookieScheme = "JobMonitorCookie";

    /// <summary>Hangfire's antiforgery token cookie, scoped to the Job monitor route.</summary>
    public const string AntiforgeryCookieName = "job_monitor_antiforgery";

    /// <summary>
    /// The route the Job monitor is mounted at — the one source for both the mapping and the cookie
    /// <c>Path</c>, which must agree or the browser never sends the cookie.
    /// </summary>
    public static string RouteFrom(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return (configuration.GetSection(nameof(HangfireOptions)).Get<HangfireOptions>() ?? new HangfireOptions()).Route;
    }
}

/// <summary>
/// Endpoint metadata marking the Job monitor. Authentication forwards to
/// <see cref="JobMonitor.CookieScheme"/> only when the matched endpoint carries it.
/// </summary>
public sealed class JobMonitorEndpointMetadata
{
    public static readonly JobMonitorEndpointMetadata Instance = new();

    private JobMonitorEndpointMetadata()
    {
    }
}