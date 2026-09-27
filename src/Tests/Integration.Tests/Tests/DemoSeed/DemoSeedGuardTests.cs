extern alias migrator;

using Boilerplate.Modules.Identity.Contracts.Services;
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
    // Length only: the shipped policy has no composition rules (ASVS V6.2.5).
    private static readonly PasswordOptions ShippedPolicy = new()
    {
        RequiredLength = 10,
        RequireDigit = false,
        RequireLowercase = false,
        RequireUppercase = false,
        RequireNonAlphanumeric = false,
    };

    private const string CommonPassword = "password123";

    private static readonly ICommonPasswordList CommonPasswords = new ListOf(CommonPassword);

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
            () => DemoSeedGuard.ResolveDemoPassword(Configured(null), ShippedPolicy, CommonPasswords));

        error.Message.ShouldContain("Seed:DemoPassword");
        // The message has to say where the value comes from, or the reader is stuck.
        error.Message.ShouldContain("SEED_DEMO_PASSWORD");
        error.Message.ShouldContain("seed-demo-password");
    }

    [Fact]
    public void ResolveDemoPassword_Should_Fail_When_ItBreaksThePasswordPolicy()
    {
        var error = Should.Throw<InvalidOperationException>(
            () => DemoSeedGuard.ResolveDemoPassword(Configured("demo"), ShippedPolicy, CommonPasswords));

        error.Message.ShouldContain("at least 10 characters");
    }

    [Fact]
    public void ResolveDemoPassword_Should_Fail_When_ItIsACommonPassword()
    {
        // UserManager would refuse it on the first demo user; the guard says so before any is written.
        var error = Should.Throw<InvalidOperationException>(
            () => DemoSeedGuard.ResolveDemoPassword(Configured(CommonPassword), ShippedPolicy, CommonPasswords));

        error.Message.ShouldContain("common-password list");
        error.Message.ShouldNotContain(CommonPassword);
    }

    [Fact]
    public void ResolveDemoPassword_Should_Return_AUsablePassword()
    {
        DemoSeedGuard.ResolveDemoPassword(Configured(TestConstants.DemoPassword), ShippedPolicy, CommonPasswords)
            .ShouldBe(TestConstants.DemoPassword);
    }

    [Fact]
    public void Validate_Should_Accept_AnAllLowercasePassphrase_UnderTheShippedPolicy()
    {
        DemoSeedGuard.Validate("quietmeadowlantern", ShippedPolicy, CommonPasswords).ShouldBeEmpty();
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

        DemoSeedGuard.Validate("DemoSeed123", strict, CommonPasswords)
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

    private sealed class ListOf(params string[] entries) : ICommonPasswordList
    {
        public bool Contains(string? password) =>
            password is not null && entries.Contains(password, StringComparer.OrdinalIgnoreCase);
    }
}
