using Boilerplate.BuildingBlocks.Web.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Identity.Authorization.Jwt;

/// <summary>
/// Environment-aware checks on <see cref="JwtOptions"/>. <see cref="JwtOptions"/>' own
/// <c>IValidatableObject</c> rules (length, the "replace-with" placeholder) run everywhere and
/// cannot see the environment; these rules only apply in Production, where a signing key that is
/// public in the repository means anyone can mint tokens for any tenant.
/// </summary>
internal sealed class JwtOptionsProductionValidator(IHostEnvironment environment) : IValidateOptions<JwtOptions>
{
    public ValidateOptionsResult Validate(string? name, JwtOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!environment.IsProduction())
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (PlaceholderSecret.Looks(options.SigningKey))
        {
            failures.Add(
                "JwtOptions: SigningKey looks like a template placeholder or a development default. " +
                "Generate a real key (e.g. `openssl rand -base64 48`) and supply it through the environment.");
        }

        if (options.Issuer.Contains("localhost", StringComparison.OrdinalIgnoreCase) ||
            options.Audience.Contains("localhost", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("JwtOptions: Issuer/Audience still point at localhost.");
        }

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }
}
