using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Boilerplate.BuildingBlocks.Web.Cors;

/// <summary>
/// Validates <see cref="CorsOptions"/> against the hosting environment. Data annotations cannot see
/// the environment, and the rule that matters here is environment-dependent: the AllowAll branch
/// echoes back any origin, which is a convenience for a laptop and an open door anywhere else.
/// </summary>
internal sealed class CorsOptionsValidator(IHostEnvironment environment) : IValidateOptions<CorsOptions>
{
    public ValidateOptionsResult Validate(string? name, CorsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        // Development only — Staging, Production and any custom environment must name their origins.
        if (options.AllowAll && !environment.IsDevelopment())
        {
            failures.Add(
                $"CorsOptions: AllowAll is permitted only in Development (environment is '{environment.EnvironmentName}'). " +
                "List the browser origins in AllowedOrigins.");
        }

        foreach (var origin in options.AllowedOrigins)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                failures.Add($"CorsOptions: AllowedOrigins entry '{origin}' is not an absolute http(s) origin.");
                continue;
            }

            if (origin.EndsWith('/'))
            {
                failures.Add($"CorsOptions: AllowedOrigins entry '{origin}' must not end with '/' — browsers send the origin without a trailing slash, so it would never match.");
            }
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
