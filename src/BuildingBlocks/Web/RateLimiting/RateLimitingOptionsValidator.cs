using Microsoft.Extensions.Options;

namespace Boilerplate.BuildingBlocks.Web.RateLimiting;

/// <summary>
/// Validates <see cref="RateLimitingOptions"/>. Data annotations cannot express "these ranges only
/// matter when Enabled is true", and a zero permit limit or window is not a tight limit but a
/// runtime <c>ArgumentOutOfRangeException</c> from the fixed-window limiter on the first request.
/// </summary>
internal sealed class RateLimitingOptionsValidator : IValidateOptions<RateLimitingOptions>
{
    public ValidateOptionsResult Validate(string? name, RateLimitingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        Check(nameof(options.Tenant), options.Tenant, failures);
        Check(nameof(options.User), options.User, failures);
        Check(nameof(options.Ip), options.Ip, failures);
        Check(nameof(options.Auth), options.Auth, failures);

        return failures.Count > 0 ? ValidateOptionsResult.Fail(failures) : ValidateOptionsResult.Success;
    }

    private static void Check(string policyName, FixedWindowPolicyOptions policy, List<string> failures)
    {
        if (policy is null)
        {
            failures.Add($"RateLimitingOptions: {policyName} policy is missing.");
            return;
        }

        if (policy.PermitLimit <= 0)
        {
            failures.Add($"RateLimitingOptions: {policyName}.PermitLimit must be greater than 0.");
        }

        if (policy.WindowSeconds <= 0)
        {
            failures.Add($"RateLimitingOptions: {policyName}.WindowSeconds must be greater than 0.");
        }

        if (policy.QueueLimit < 0)
        {
            failures.Add($"RateLimitingOptions: {policyName}.QueueLimit must not be negative.");
        }
    }
}
