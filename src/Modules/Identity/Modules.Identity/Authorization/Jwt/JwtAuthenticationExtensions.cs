using Microsoft.AspNetCore.Authentication.JwtBearer;
using Boilerplate.BuildingBlocks.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Identity.Authorization.Jwt;

internal static class JwtAuthenticationExtensions
{
    internal static IServiceCollection ConfigureJwtAuth(this IServiceCollection services)
    {
        services.AddOptions<JwtOptions>()
            .BindConfiguration(nameof(JwtOptions))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsProductionValidator>();

        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearerOptions>();
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJobMonitorCookieOptions>();
        services
            .AddAuthentication(authentication =>
            {
                // Bearer for every request except a Job monitor request carrying the Job monitor
                // cookie and no bearer header (ADR-0009) — see JobMonitorCookieAuthentication.
                authentication.DefaultAuthenticateScheme = JobMonitorCookieAuthentication.SelectorScheme;
                authentication.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(JwtBearerDefaults.AuthenticationScheme, null!)
            .AddJwtBearer(JobMonitor.CookieScheme, null!)
            .AddPolicyScheme(JobMonitorCookieAuthentication.SelectorScheme, displayName: null, options =>
                options.ForwardDefaultSelector = JobMonitorCookieAuthentication.Select);

        services.AddAuthorizationBuilder().AddRequiredPermissionPolicy();
        services.AddAuthorization(options =>
        {
            // Permission evaluation lives in the RequiredPermission policy (it reads each endpoint's
            // RequiredPermissionAttribute / AuthenticatedOnlyAttribute metadata and fails closed when
            // an endpoint declares neither). Wire it as BOTH the default AND the fallback policy:
            //   - FallbackPolicy covers endpoints with no auth metadata at all, plus requests that
            //     match no endpoint — both are denied by the handler.
            //   - DefaultPolicy covers endpoints that opt in via .RequireAuthorization() —
            //     including the module route-groups (Files/Notifications/…). Without
            //     this, a group-level .RequireAuthorization() applied the built-in
            //     authenticated-only default, which SUPPRESSED the fallback, so
            //     .RequirePermission(...) was never evaluated and any authenticated tenant
            //     member could perform gated writes. Both must point at the permission policy.
            // .AllowAnonymous() still wins: the authorization middleware short-circuits before the
            // policy runs, so anonymous endpoints never reach the handler.
            options.DefaultPolicy = options.GetPolicy(RequiredPermissionDefaults.PolicyName)!;
            options.FallbackPolicy = options.GetPolicy(RequiredPermissionDefaults.PolicyName);
        });
        return services;
    }
}