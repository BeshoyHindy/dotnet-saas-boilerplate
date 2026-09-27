using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using System;
using System.Linq;
using AspNetCorsOptions = Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions;

namespace Boilerplate.BuildingBlocks.Web.Cors;

public static class Extensions
{
    private const string PolicyName = "AppCorsPolicy";

    public static IServiceCollection AddHeroCors(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<CorsOptions>()
            .Bind(configuration.GetSection(nameof(CorsOptions)))
            .Validate(settings => settings.AllowAll || settings.AllowedOrigins.Length > 0, "CorsOptions: AllowedOrigins are required when AllowAll is false.")
            .Validate(settings => settings.AllowAll || settings.AllowedHeaders.Length > 0, "CorsOptions: AllowedHeaders are required when AllowAll is false.")
            .Validate(settings => settings.AllowAll || settings.AllowedMethods.Length > 0, "CorsOptions: AllowedMethods are required when AllowAll is false.")
            .ValidateOnStart();

        // Environment-aware rules (AllowAll is a Production footgun, origins must be real origins)
        // live in a validator because inline .Validate lambdas cannot resolve IHostEnvironment.
        services.AddSingleton<IValidateOptions<CorsOptions>, CorsOptionsValidator>();

        services.AddCors();
        services.AddSingleton<IConfigureOptions<AspNetCorsOptions>>(sp =>
        {
            var corsSettings = sp.GetRequiredService<IOptions<CorsOptions>>();
            return new ConfigureOptions<AspNetCorsOptions>(options =>
            {
                options.AddPolicy(PolicyName, builder =>
                {
                    var settings = corsSettings.Value;

                    // No AllowCredentials in either branch: authentication is a bearer header, and no
                    // client sends cookies or `withCredentials` (the credentialed SignalR negotiate
                    // that once justified it is gone). Asking for credentials would mean the browser
                    // attaches cookies cross-origin — risk with nothing using it.
                    if (settings.AllowAll)
                    {
                        // Development-only (CorsOptionsValidator rejects it elsewhere). Echo the
                        // request origin rather than `*` so the reflected value is visible in traces.
                        builder
                            .SetIsOriginAllowed(_ => true)
                            .AllowAnyHeader()
                            .AllowAnyMethod();
                    }
                    else
                    {
                        builder
                            .WithOrigins(settings.AllowedOrigins)
                            .WithHeaders(WithIfMatch(settings.AllowedHeaders))
                            .WithMethods(settings.AllowedMethods);
                    }

                    // The profile's version travels as ETag out and If-Match back (#107). A browser
                    // hides a response header from script unless it is exposed — AllowAnyHeader
                    // covers request headers only — so both branches expose it.
                    builder.WithExposedHeaders(HeaderNames.ETag);
                });
            });
        });

        return services;
    }

    /// <summary>
    /// The configured request headers plus <c>If-Match</c>, which the clients always send on a
    /// profile update (#107). A lone <c>*</c> is left alone: CORS treats the list as "any header"
    /// only while it is exactly that, so appending to it would narrow the policy.
    /// </summary>
    internal static string[] WithIfMatch(string[] allowedHeaders)
    {
        ArgumentNullException.ThrowIfNull(allowedHeaders);

        if (allowedHeaders is ["*"]
            || allowedHeaders.Contains(HeaderNames.IfMatch, StringComparer.OrdinalIgnoreCase))
        {
            return allowedHeaders;
        }

        return [.. allowedHeaders, HeaderNames.IfMatch];
    }

    public static void UseHeroCors(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        app.UseCors(PolicyName);
    }
}