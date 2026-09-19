using Boilerplate.BuildingBlocks.Caching;
using Boilerplate.BuildingBlocks.Jobs;
using Boilerplate.BuildingBlocks.Mailing;
using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Web.Auth;
using Boilerplate.BuildingBlocks.Web.Cors;
using Boilerplate.BuildingBlocks.Web.Exceptions;
using Boilerplate.BuildingBlocks.Web.Idempotency;
using Boilerplate.BuildingBlocks.Web.Health;
using Boilerplate.BuildingBlocks.Web.Limits;
using Boilerplate.BuildingBlocks.Web.Mediator.Behaviors;
using Boilerplate.BuildingBlocks.Web.Modules;
using Boilerplate.BuildingBlocks.Web.Observability.Logging.Serilog;
using Boilerplate.BuildingBlocks.Web.Observability.OpenTelemetry;
using Boilerplate.BuildingBlocks.Web.OpenApi;
using Boilerplate.BuildingBlocks.Web.Origin;
using Boilerplate.BuildingBlocks.Web.RateLimiting;
using Boilerplate.BuildingBlocks.Web.Security;
using Boilerplate.BuildingBlocks.Web.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Mediator;

namespace Boilerplate.BuildingBlocks.Web;

public static class Extensions
{
    public static IHostApplicationBuilder AddHeroPlatform(this IHostApplicationBuilder builder, Action<AppPlatformOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new AppPlatformOptions();
        configure?.Invoke(options);

        // Publish the resolved options so modules configured later in the same build can honour them.
        // IHostApplicationBuilder.Properties exists for exactly this — passing state between builder
        // extensions — and keeps the flags out of DI, where a module would have to scan descriptors.
        builder.SetHeroPlatformOptions(options);

        builder.Services.AddPermissions(SystemPermissions.All);

        builder.Services.AddScoped<CurrentUserMiddleware>();

        builder.Services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.Providers.Add<BrotliCompressionProvider>();
            options.Providers.Add<GzipCompressionProvider>();
        });
        builder.Services.Configure<BrotliCompressionProviderOptions>(options =>
        {
            options.Level = System.IO.Compression.CompressionLevel.Fastest;
        });

        builder.AddHeroLogging();
        if (options.EnableOpenTelemetry)
        {
            builder.AddHeroOpenTelemetry();
        }

        builder.Services.AddHttpContextAccessor();
        builder.Services.AddHeroDatabaseOptions(builder.Configuration);
        builder.Services.AddHeroRateLimiting(builder.Configuration);

        var corsEnabled = options.EnableCors && IsCorsEnabled(builder.Configuration);
        var openApiEnabled = options.EnableOpenApi && IsOpenApiEnabled(builder.Configuration);

        if (corsEnabled)
        {
            builder.Services.AddHeroCors(builder.Configuration);
        }

        builder.Services.AddHeroVersioning();

        if (openApiEnabled)
        {
            builder.Services.AddHeroOpenApi(builder.Configuration);
        }

        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [HealthTags.Live, HealthTags.Ready]);

        if (options.EnableJobs)
        {
            builder.Services.AddHeroJobs();
            // Not a readiness dependency: background processing can be down while the API still
            // answers requests, and the check queries Hangfire storage.
            builder.Services.AddHealthChecks().AddCheck<HangfireHealthCheck>("hangfire");
        }

        if (options.EnableMailing)
        {
            builder.Services.AddHeroMailing();
        }

        if (options.EnableCaching)
        {
            builder.Services.AddHeroCaching(builder.Configuration);
            var cacheConfig = builder.Configuration.GetSection(nameof(CachingOptions)).Get<CachingOptions>();
            if (cacheConfig is not null && !string.IsNullOrEmpty(cacheConfig.Redis))
            {
                builder.Services.AddHealthChecks().AddCheck<RedisHealthCheck>("redis", tags: [HealthTags.Ready]);
            }
        }

        if (options.EnableIdempotency)
        {
            builder.Services.AddHeroIdempotency(builder.Configuration);
        }

        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        builder.Services.AddProblemDetails();
        builder.Services.AddOptions<OriginOptions>().BindConfiguration(nameof(OriginOptions));
        builder.Services.AddOptions<SecurityHeadersOptions>().BindConfiguration(nameof(SecurityHeadersOptions));

        // Kestrel request limits: bound here rather than left to the framework defaults so a single
        // request can't stream 30 MB into memory on an API whose uploads bypass it entirely.
        builder.Services.AddOptions<RequestLimitsOptions>()
            .BindConfiguration(RequestLimitsOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddSingleton<IConfigureOptions<KestrelServerOptions>, ConfigureRequestLimits>();

        // Reverse-proxy (Traefik) forwarded headers.
        builder.Services.AddOptions<ProxyOptions>()
            .BindConfiguration(ProxyOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();
        builder.Services.AddSingleton<IValidateOptions<ProxyOptions>, ProxyOptionsValidator>();
        builder.Services.AddSingleton<IConfigureOptions<ForwardedHeadersOptions>, ConfigureForwardedHeaders>();

        return builder;
    }


    public static WebApplication UseHeroPlatform(this WebApplication app, Action<AppPipelineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = new AppPipelineOptions();
        configure?.Invoke(options);

        var corsEnabled = options.UseCors && IsCorsEnabled(app.Configuration);
        var openApiEnabled = options.UseOpenApi && IsOpenApiEnabled(app.Configuration);

        // Forwarded headers run FIRST: every later decision that reads the scheme or the client IP
        // (HTTPS redirect, IP rate-limit partitions, audit trails) must see the caller's values, not
        // the proxy's.
        if (app.Services.GetRequiredService<IOptions<ProxyOptions>>().Value.Enabled)
        {
            app.UseForwardedHeaders();
        }

        app.UseExceptionHandler();
        app.UseResponseCompression();

        // CORS MUST run before UseHttpsRedirection: preflight OPTIONS can't follow an HTTP→HTTPS redirect, so
        // the browser would block the call. Safe before routing because we use one global policy (no [EnableCors]).
        if (corsEnabled)
        {
            app.UseHeroCors();
        }

        app.UseHttpsRedirection();

        app.UseHeroSecurityHeaders();

        // Serve static files as early as possible to short-circuit pipeline
        if (options.ServeStaticFiles)
        {
            var assetsPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot");
            if (!Directory.Exists(assetsPath))
            {
                Directory.CreateDirectory(assetsPath);
            }

            app.UseStaticFiles();
        }

        app.UseRouting();

        if (openApiEnabled)
        {
            app.UseHeroOpenApi();
        }

        app.UseAuthentication();

        // Let each module register its own middleware (e.g. Auditing registers AuditHttpMiddleware)
        app.UseModuleMiddlewares();

        app.UseHeroRateLimiting();

        app.UseAuthorization();

        if (options.MapModules)
        {
            app.MapModules();
        }

        // Always expose health endpoints
        app.MapHeroHealthEndpoints();

        // Mapped here (not as pre-routing middleware) so the dashboard runs behind UseAuthentication
        // and UseAuthorization and is gated by SystemPermissions.Hangfire.View like any other endpoint.
        app.MapHeroJobDashboard(app.Configuration);

        app.UseMiddleware<CurrentUserMiddleware>();
        return app;
    }

    private static bool IsCorsEnabled(IConfiguration configuration)
    {
        var allowAll = configuration.GetValue("CorsOptions:AllowAll", false);
        var origins = configuration.GetSection("CorsOptions:AllowedOrigins").Get<string[]>() ?? [];
        return allowAll || origins.Length > 0;
    }

    private static bool IsOpenApiEnabled(IConfiguration configuration)
    {
        return configuration.GetValue("OpenApiOptions:Enabled", true);
    }
}

public sealed class AppPlatformOptions
{
    public bool EnableCors { get; set; } = true;
    public bool EnableOpenApi { get; set; } = true;
    public bool EnableCaching { get; set; } = false;
    public bool EnableJobs { get; set; } = false;
    public bool EnableMailing { get; set; } = false;
    public bool EnableOpenTelemetry { get; set; } = true;
    public bool EnableIdempotency { get; set; } = true;

    /// <summary>
    /// Registers JWT bearer authentication and the authorization policies. Turn it off in a host that
    /// loads the modules for their data access but never authenticates a caller — the DbMigrator. Such
    /// a host has no signing key, and requiring one would mean either shipping a placeholder (a secret
    /// a Production validator has to be taught to ignore) or handing the migrator the API's key.
    /// </summary>
    public bool EnableAuthentication { get; set; } = true;
}

/// <summary>
/// Lets a module read the <see cref="AppPlatformOptions"/> the host chose in
/// <c>AddHeroPlatform</c>. Modules only receive the <see cref="IHostApplicationBuilder"/>, so the
/// options travel in its <see cref="IHostApplicationBuilder.Properties"/> bag.
/// </summary>
public static class AppPlatformOptionsExtensions
{
    private const string PropertyKey = "Boilerplate.BuildingBlocks.Web.AppPlatformOptions";

    /// <summary>
    /// Publishes the options for modules configured later in the same build. <c>AddHeroPlatform</c>
    /// calls this; a host that composes modules without the full platform can call it directly.
    /// </summary>
    public static IHostApplicationBuilder SetHeroPlatformOptions(this IHostApplicationBuilder builder, AppPlatformOptions options)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(options);

        builder.Properties[PropertyKey] = options;
        return builder;
    }

    /// <summary>
    /// The options the host configured, or the defaults when a host wires modules without calling
    /// <c>AddHeroPlatform</c> — defaults leave every feature in the state it had before the flag
    /// existed, so a module that asks is never worse off than one that does not.
    /// </summary>
    public static AppPlatformOptions GetHeroPlatformOptions(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder.Properties.TryGetValue(PropertyKey, out var value) && value is AppPlatformOptions options
            ? options
            : new AppPlatformOptions();
    }
}

public sealed class AppPipelineOptions
{
    public bool UseCors { get; set; } = true;
    public bool UseOpenApi { get; set; } = true;
    public bool ServeStaticFiles { get; set; } = true;
    public bool MapModules { get; set; } = true;
}