using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Boilerplate.DbMigrator.DemoSeed;

/// <summary>
/// The two questions asked before any demo data is written: may this environment have demo
/// accounts at all, and is the one password they will all share usable?
///
/// Separate from <see cref="DemoSeeder"/> because both answers are wanted twice — once by the
/// migrator's entry point, which refuses before touching a database, and once by the seeder
/// itself, so the class is safe to call directly (tests do).
/// </summary>
internal static class DemoSeedGuard
{
    /// <summary>Configuration key holding the password every demo account signs in with.</summary>
    public const string DemoPasswordKey = "Seed:DemoPassword";

    /// <summary>
    /// Thrown message when <c>--demo</c> meets a Production host. Demo seeding mints well-known
    /// accounts sharing one password that a developer reads off a dashboard — in Production that
    /// is not a convenience, it is a back door. There is no override flag on purpose.
    /// </summary>
    public const string ProductionRefusal =
        "Demo seeding (--demo) is refused in Production. It creates the well-known 'acme' and 'globex' tenants "
        + "whose accounts all share one configured password, which is a back door in a real deployment. "
        + "Re-run the migrator without --demo.";

    /// <summary>Throws when <paramref name="environment"/> is Production.</summary>
    /// <exception cref="InvalidOperationException">Demo seeding was requested in Production.</exception>
    public static void EnsureEnvironmentAllowsDemoSeeding(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        if (environment.IsProduction())
        {
            throw new InvalidOperationException(ProductionRefusal);
        }
    }

    /// <summary>
    /// Reads <c>Seed:DemoPassword</c> and checks it against the host's own Identity password
    /// policy, so a password the seeder would accept and <c>UserManager</c> would then reject
    /// fails here — with the reason — instead of half-way through the tenant loop.
    /// </summary>
    /// <exception cref="InvalidOperationException">The password is missing or unusable.</exception>
    public static string ResolveDemoPassword(IConfiguration configuration, PasswordOptions policy)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var password = RequireConfiguredPassword(configuration);

        var failures = Validate(password, policy);
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Configuration '{DemoPasswordKey}' does not satisfy this host's Identity password policy: "
                + string.Join(" ", failures));
        }

        return password;
    }

    /// <summary>
    /// Presence-only check, for the entry point: it runs before the host is built, so no password
    /// policy is available yet, but a missing password should still stop the run before migrations.
    /// </summary>
    /// <exception cref="InvalidOperationException">The password is missing.</exception>
    public static string RequireConfiguredPassword(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var password = configuration[DemoPasswordKey];
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException(
                $"Demo seeding (--demo) needs '{DemoPasswordKey}' — every demo account signs in with it. "
                + "Set Seed__DemoPassword: the Aspire AppHost passes the generated 'seed-demo-password' "
                + "parameter (read it in the dashboard under Parameters), and docker compose passes "
                + "SEED_DEMO_PASSWORD from .env (run scripts/local-env.sh to generate one).");
        }

        return password;
    }

    /// <summary>
    /// Every way <paramref name="candidate"/> fails <paramref name="policy"/>, phrased for an
    /// operator. Empty means usable. Mirrors ASP.NET Core's <c>PasswordValidator</c>, which
    /// cannot be used directly here because it needs a tenant-scoped <c>UserManager</c>.
    /// </summary>
    public static IReadOnlyList<string> Validate(string? candidate, PasswordOptions policy)
    {
        ArgumentNullException.ThrowIfNull(policy);

        var failures = new List<string>();
        var password = candidate ?? string.Empty;

        if (password.Length < policy.RequiredLength)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"It must be at least {policy.RequiredLength} characters."));
        }

        if (policy.RequireDigit && !password.Any(char.IsDigit))
        {
            failures.Add("It must contain a digit.");
        }

        if (policy.RequireLowercase && !password.Any(char.IsLower))
        {
            failures.Add("It must contain a lowercase letter.");
        }

        if (policy.RequireUppercase && !password.Any(char.IsUpper))
        {
            failures.Add("It must contain an uppercase letter.");
        }

        if (policy.RequireNonAlphanumeric && password.All(char.IsLetterOrDigit))
        {
            failures.Add("It must contain a non-alphanumeric character.");
        }

        if (policy.RequiredUniqueChars > 1 && password.Distinct().Count() < policy.RequiredUniqueChars)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"It must use at least {policy.RequiredUniqueChars} distinct characters."));
        }

        return failures;
    }
}
