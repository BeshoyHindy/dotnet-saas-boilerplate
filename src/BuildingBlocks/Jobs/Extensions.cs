using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Jobs.Services;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Identity.Authorization;
using Boilerplate.BuildingBlocks.Shared.Persistence;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Boilerplate.BuildingBlocks.Jobs;

public static class Extensions
{
    public static IServiceCollection AddHeroJobs(this IServiceCollection services)
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

            config.UseActivator(new AppJobActivator(provider.GetRequiredService<IServiceScopeFactory>()));
            config.UseFilter(new AppJobFilter(provider));
            config.UseFilter(new LogJobFilter());
            config.UseFilter(new HangfireTelemetryFilter());
        });

        // Deferred stale lock cleanup — runs after app starts accepting requests
        services.AddHostedService<HangfireStaleLockCleanupService>();

        services.AddTransient<IJobService, HangfireService>();

        return services;
    }


    /// <summary>
    /// Mounts the Hangfire dashboard as a routed endpoint gated by
    /// <see cref="SystemPermissions.Hangfire.View"/>. Must be called after
    /// <c>UseAuthentication()</c>/<c>UseAuthorization()</c> so the platform's own authentication and
    /// permission policy are the gate — there is no separate dashboard credential.
    /// </summary>
    public static IEndpointRouteBuilder MapHeroJobDashboard(this IEndpointRouteBuilder endpoints, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(config);

        var hangfireOptions = config.GetSection(nameof(HangfireOptions)).Get<HangfireOptions>() ?? new HangfireOptions();

        var dashboardOptions = new DashboardOptions
        {
            AppPath = "/",

            // Deliberately empty. Hangfire's default filter chain is LocalRequestsOnly, which would
            // reject every remote operator the permission policy just allowed; ASP.NET Core
            // authorization is the single gate.
            Authorization = [],
        };

        endpoints.MapHangfireDashboard(hangfireOptions.Route, dashboardOptions)
            .RequirePermission(SystemPermissions.Hangfire.View);

        return endpoints;
    }
}