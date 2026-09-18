using Boilerplate.BuildingBlocks.Caching;
using Boilerplate.BuildingBlocks.Jobs;
using Boilerplate.BuildingBlocks.Mailing;
using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Web.Auth;
using Boilerplate.BuildingBlocks.Web.Cors;
using Boilerplate.BuildingBlocks.Web.Exceptions;
using Boilerplate.BuildingBlocks.Web.FeatureFlags;
using Boilerplate.BuildingBlocks.Web.Idempotency;
using Boilerplate.BuildingBlocks.Web.Health;
using Boilerplate.BuildingBlocks.Web.Mediator.Behaviors;
using Boilerplate.BuildingBlocks.Web.Modules;
using Boilerplate.BuildingBlocks.Web.Observability.Logging.Serilog;
using Boilerplate.BuildingBlocks.Web.Observability.OpenTelemetry;
using Boilerplate.BuildingBlocks.Web.OpenApi;
using Boilerplate.BuildingBlocks.Web.Origin;
using Boilerplate.BuildingBlocks.Web.RateLimiting;
using Boilerplate.BuildingBlocks.Web.Realtime;
using Boilerplate.BuildingBlocks.Web.Security;
using Boilerplate.BuildingBlocks.Web.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Mediator;

namespace Boilerplate.BuildingBlocks.Web;

public static class Extensions
{
    public static IHostApplicationBuilder AddHeroPlatform(this IHostApplicationBuilder builder, Action<AppPlatformOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new AppPlatformOptions();
        configure?.Invoke(options);

        PermissionConstants.Register(SystemPermissions.All);

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

        builder.Services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy());

        if (options.EnableJobs)
        {
            builder.Services.AddHeroJobs();
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
                builder.Services.AddHealthChecks().AddCheck<RedisHealthCheck>("redis");
            }
        }

        if (options.EnableFeatureFlags)
        {
            builder.Services.AddHeroFeatureFlags(builder.Configuration);
        }

        if (options.EnableIdempotency)
        {
            builder.Services.AddHeroIdempotency(builder.Configuration);
        }

        if (options.EnableRealtime)
        {
            builder.Services.AddHeroRealtime(builder.Configuration);
        }

        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
        builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
        builder.Services.AddProblemDetails();
        builder.Services.AddOptions<OriginOptions>().BindConfiguration(nameof(OriginOptions));
        builder.Services.AddOptions<SecurityHeadersOptions>().BindConfiguration(nameof(SecurityHeadersOptions));

        return builder;
    }


    public static WebApplication UseHeroPlatform(this WebApplication app, Action<AppPipelineOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(app);

        var options = new AppPipelineOptions();
        configure?.Invoke(options);

        var corsEnabled = options.UseCors && IsCorsEnabled(app.Configuration);
        var openApiEnabled = options.UseOpenApi && IsOpenApiEnabled(app.Configuration);

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

        app.UseHeroJobDashboard(app.Configuration);
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

        if (options.MapRealtime)
        {
            app.MapHeroRealtime();
        }
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
    public bool EnableFeatureFlags { get; set; } = false;
    public bool EnableIdempotency { get; set; } = true;
    public bool EnableRealtime { get; set; } = false;
}

public sealed class AppPipelineOptions
{
    public bool UseCors { get; set; } = true;
    public bool UseOpenApi { get; set; } = true;
    public bool ServeStaticFiles { get; set; } = true;
    public bool MapModules { get; set; } = true;
    public bool MapRealtime { get; set; } = false;
}