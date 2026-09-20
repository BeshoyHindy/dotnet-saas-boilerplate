extern alias migrator;

using Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using migrator::Boilerplate.DbMigrator.DemoSeed;

namespace Integration.Tests.Tests.DemoSeed;

/// <summary>
/// The two refusals that stand between <c>--demo</c> and a database. No containers and no host:
/// these are pure decisions, and they are the ones that must never quietly stop working.
///
/// Production is the important one. Demo seeding mints well-known tenants whose every account
/// shares one configured password; in a real deployment that is a back door, so the flag is
/// refused outright rather than gated behind another flag.
/// </summary>
public sealed class DemoSeedGuardTests
{
    private static readonly PasswordOptions ShippedPolicy = new()
    {
        RequiredLength = 10,
        RequireDigit = true,
        RequireLowercase = true,
        RequireUppercase = true,
        RequireNonAlphanumeric = false,
    };

    [Fact]
    public void EnsureEnvironmentAllowsDemoSeeding_Should_Refuse_Production()
    {
        var environment = EnvironmentNamed(Environments.Production);

        var error = Should.Throw<InvalidOperationException>(
            () => DemoSeedGuard.EnsureEnvironmentAllowsDemoSeeding(environment));

        error.Message.ShouldContain("refused in Production");
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Staging")]
    [InlineData("Local")]
    public void EnsureEnvironmentAllowsDemoSeeding_Should_Allow_EverythingElse(string environmentName)
    {
        Should.NotThrow(
            () => DemoSeedGuard.EnsureEnvironmentAllowsDemoSeeding(EnvironmentNamed(environmentName)));
    }

    [Fact]
    public void ResolveDemoPassword_Should_Fail_When_NotConfigured()
    {
        var error = Should.Throw<InvalidOperationException>(
            () => DemoSeedGuard.ResolveDemoPassword(Configured(null), ShippedPolicy));

        error.Message.ShouldContain("Seed:DemoPassword");
        // The message has to say where the value comes from, or the reader is stuck.
        error.Message.ShouldContain("SEED_DEMO_PASSWORD");
        error.Message.ShouldContain("seed-demo-password");
    }

    [Fact]
    public void ResolveDemoPassword_Should_Fail_When_ItBreaksThePasswordPolicy()
    {
        // Short, no digit, no uppercase — every rule at once, so the message lists them all.
        var error = Should.Throw<InvalidOperationException>(
            () => DemoSeedGuard.ResolveDemoPassword(Configured("demo"), ShippedPolicy));

        error.Message.ShouldContain("at least 10 characters");
        error.Message.ShouldContain("a digit");
        error.Message.ShouldContain("an uppercase letter");
    }

    [Fact]
    public void ResolveDemoPassword_Should_Return_AUsablePassword()
    {
        DemoSeedGuard.ResolveDemoPassword(Configured(TestConstants.DemoPassword), ShippedPolicy)
            .ShouldBe(TestConstants.DemoPassword);
    }

    [Fact]
    public void Validate_Should_Honour_ANonAlphanumericRequirement()
    {
        var strict = new PasswordOptions
        {
            RequiredLength = 10,
            RequireDigit = true,
            RequireLowercase = true,
            RequireUppercase = true,
            RequireNonAlphanumeric = true,
        };

        DemoSeedGuard.Validate("DemoSeed123", strict)
            .ShouldContain(f => f.Contains("non-alphanumeric", StringComparison.Ordinal));
    }

    private static HostingEnvironment EnvironmentNamed(string name) =>
        new HostingEnvironment { EnvironmentName = name, ApplicationName = "Boilerplate.DbMigrator" };

    private static IConfiguration Configured(string? demoPassword) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DemoSeedGuard.DemoPasswordKey] = demoPassword,
            })
            .Build();
}
