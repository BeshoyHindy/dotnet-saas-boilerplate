using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Boilerplate.BuildingBlocks.Web.Cors;

/// <summary>
/// Validates <see cref="CorsOptions"/> against the hosting environment. Data annotations cannot see
/// the environment, and the dangerous combination here is environment-dependent: the AllowAll branch
/// echoes any origin back with <c>AllowCredentials</c>, which in Production hands every website a
/// credentialed cross-origin channel to the API.
/// </summary>
internal sealed class CorsOptionsValidator(IHostEnvironment environment) : IValidateOptions<CorsOptions>
{
    public ValidateOptionsResult Validate(string? name, CorsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.AllowAll && environment.IsProduction())
        {
            failures.Add("CorsOptions: AllowAll must be false in Production. List the browser origins in AllowedOrigins.");
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
