using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Boilerplate.BuildingBlocks.Web.Configuration;

/// <summary>
/// Startup fail-fast for Production configuration (ADR-0005): the host refuses to boot when a
/// required setting is missing, when a secret still holds a template placeholder, or when the host
/// allow-list is open. Options validators cover one section each; this covers the keys that belong
/// to no single options type and the environment-specific rules.
/// </summary>
public static class ProductionConfigurationGuard
{
    /// <summary>Keys that must carry a value in Production.</summary>
    private static readonly string[] RequiredKeys =
    [
        "DatabaseOptions:ConnectionString",
        "CachingOptions:Redis",
        "JwtOptions:SigningKey",
    ];

    /// <summary>
    /// Opaque secrets that must not hold a placeholder when set. Absent keys are fine — a host that
    /// does not use the feature (e.g. the API never seeds) should not be forced to configure it.
    /// </summary>
    private static readonly string[] SecretKeys =
    [
        "JwtOptions:SigningKey",
        "Seed:DefaultAdminPassword",
        "MailOptions:SMTP:Password",
        "MailOptions:SendGrid:ApiKey",
        "Storage:S3:SecretKey",
    ];

    /// <summary>
    /// Throws when <paramref name="builder"/> runs in Production with unusable configuration.
    /// No-op in every other environment.
    /// </summary>
    public static IHostApplicationBuilder ValidateProductionConfiguration(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!builder.Environment.IsProduction())
        {
            return builder;
        }

        var failures = Inspect(builder.Configuration);
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                "Production configuration is not usable:" + Environment.NewLine +
                string.Join(Environment.NewLine, failures.Select(f => "  - " + f)));
        }

        return builder;
    }

    /// <summary>
    /// Returns every Production configuration problem found in <paramref name="configuration"/>.
    /// Exposed separately so the rules are unit-testable without a host.
    /// </summary>
    public static IReadOnlyList<string> Inspect(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var failures = new List<string>();

        failures.AddRange(RequiredKeys
            .Where(key => string.IsNullOrWhiteSpace(configuration[key]))
            .Select(key => $"Missing required configuration '{key}'."));

        failures.AddRange(SecretKeys
            .Where(key => !string.IsNullOrWhiteSpace(configuration[key]) && PlaceholderSecret.Looks(configuration[key]))
            .Select(key => $"Configuration '{key}' still holds a template placeholder; supply a real secret via environment variables or a secret store."));

        failures.AddRange(InspectStorage(configuration));

        // Host filtering is driven by this key; "*" (the framework default) accepts any Host header,
        // which lets a poisoned Host reach link generation and password-reset URLs.
        var allowedHosts = configuration["AllowedHosts"];
        if (string.IsNullOrWhiteSpace(allowedHosts))
        {
            failures.Add("Missing required configuration 'AllowedHosts'. List the hostnames this API answers on, semicolon-separated.");
        }
        else if (allowedHosts.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Any(host => host == "*"))
        {
            failures.Add("Configuration 'AllowedHosts' contains '*'; name the hostnames this API answers on instead.");
        }

        return failures;
    }

    /// <summary>
    /// Storage rules for Production. The Local provider writes into <c>wwwroot</c> and serves every
    /// object through <c>UseStaticFiles</c> — no signing, no visibility check — so a Production host
    /// running it publishes every Files-module object anonymously. It is dev-only; a deployment that
    /// genuinely wants it (a single-node box with no object store, behind its own gate) must say so
    /// with <c>Storage:AllowLocalProviderInProduction=true</c>. The bucket check keeps the s3 default
    /// honest: without it the miswiring only surfaces on the first upload.
    /// </summary>
    private static IEnumerable<string> InspectStorage(IConfiguration configuration)
    {
        var provider = configuration["Storage:Provider"];
        var isS3 = string.Equals(provider, "s3", StringComparison.OrdinalIgnoreCase);

        if (isS3)
        {
            if (string.IsNullOrWhiteSpace(configuration["Storage:S3:Bucket"]))
            {
                yield return "Missing required configuration 'Storage:S3:Bucket'; the s3 storage provider cannot address a bucket without it.";
            }

            yield break;
        }

        if (bool.TryParse(configuration["Storage:AllowLocalProviderInProduction"], out var optedIn) && optedIn)
        {
            yield break;
        }

        var named = string.IsNullOrWhiteSpace(provider) ? "<unset>" : provider;
        yield return
            $"Configuration 'Storage:Provider' is '{named}', which selects the Local provider. Local storage serves every " +
            "object anonymously from wwwroot with no signing and no visibility enforcement, publishing private files. " +
            "Set 'Storage:Provider' to 's3', or opt in explicitly with 'Storage:AllowLocalProviderInProduction=true'.";
    }
}
