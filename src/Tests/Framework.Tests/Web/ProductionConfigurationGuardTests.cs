using Boilerplate.BuildingBlocks.Web.Configuration;
using Microsoft.Extensions.Configuration;

namespace Framework.Tests.Web;

/// <summary>
/// The Production fail-fast rules (ADR-0005): a host must not boot with missing settings, a
/// placeholder secret or an open host allow-list.
/// </summary>
public sealed class ProductionConfigurationGuardTests
{
    private static IConfiguration Config(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string?>
        {
            // No password in the fixture: the guard only checks that the key is non-empty, and a
            // credential-shaped literal here would trip the repository's own secret scan.
            ["DatabaseOptions:ConnectionString"] = "Host=db;Database=app;Username=app",
            ["CachingOptions:Redis"] = "cache:6379",
            ["JwtOptions:SigningKey"] = "8Kq2f1nT0xVb9aMw3hLp6ZcR5yGdE7jU4sNiOo1v",
            ["AllowedHosts"] = "api.example.com",
        };

        foreach (var (key, value) in overrides)
        {
            values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    #region Happy Path

    [Fact]
    public void Inspect_Should_ReturnNoFailures_When_ProductionConfigurationIsComplete()
    {
        // Act
        var failures = ProductionConfigurationGuard.Inspect(Config());

        // Assert
        failures.ShouldBeEmpty();
    }

    [Fact]
    public void Inspect_Should_AcceptMultipleHosts_When_AllowedHostsListsThem()
    {
        // Act
        var failures = ProductionConfigurationGuard.Inspect(Config(("AllowedHosts", "api.example.com;admin.example.com")));

        // Assert
        failures.ShouldBeEmpty();
    }

    #endregion

    #region Exception

    [Theory]
    [InlineData("DatabaseOptions:ConnectionString")]
    [InlineData("CachingOptions:Redis")]
    [InlineData("JwtOptions:SigningKey")]
    public void Inspect_Should_ReportMissingKey_When_RequiredSettingIsEmpty(string key)
    {
        // Act
        var failures = ProductionConfigurationGuard.Inspect(Config((key, "")));

        // Assert
        failures.ShouldContain(f => f.Contains(key, StringComparison.Ordinal) && f.Contains("Missing required", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_Should_ReportPlaceholder_When_SigningKeyIsADevelopmentDefault()
    {
        // Act
        var failures = ProductionConfigurationGuard.Inspect(
            Config(("JwtOptions:SigningKey", "boilerplate-dev-only-do-not-use-in-prod-32+chars-min")));

        // Assert
        failures.ShouldContain(f => f.Contains("JwtOptions:SigningKey", StringComparison.Ordinal) && f.Contains("placeholder", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_Should_ReportPlaceholder_When_SeedPasswordIsSetToASample()
    {
        // Act
        var failures = ProductionConfigurationGuard.Inspect(Config(("Seed:DefaultAdminPassword", "ChangeMe123!")));

        // Assert
        failures.ShouldContain(f => f.Contains("Seed:DefaultAdminPassword", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_Should_ReportWildcard_When_AllowedHostsIsStar()
    {
        // Act
        var failures = ProductionConfigurationGuard.Inspect(Config(("AllowedHosts", "*")));

        // Assert
        failures.ShouldContain(f => f.Contains("AllowedHosts", StringComparison.Ordinal) && f.Contains("'*'", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_Should_ReportWildcard_When_AllowedHostsMixesStarWithRealHosts()
    {
        // Act
        var failures = ProductionConfigurationGuard.Inspect(Config(("AllowedHosts", "api.example.com;*")));

        // Assert
        failures.ShouldContain(f => f.Contains("AllowedHosts", StringComparison.Ordinal) && f.Contains("'*'", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_Should_ReportMissingHosts_When_AllowedHostsIsEmpty()
    {
        // Act
        var failures = ProductionConfigurationGuard.Inspect(Config(("AllowedHosts", "")));

        // Assert
        failures.ShouldContain(f => f.Contains("AllowedHosts", StringComparison.Ordinal) && f.Contains("Missing required", StringComparison.Ordinal));
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Inspect_Should_IgnoreUnsetOptionalSecrets_When_TheHostDoesNotUseThem()
    {
        // Act — the API never seeds and may send no mail; absent keys must not block a boot.
        var failures = ProductionConfigurationGuard.Inspect(Config());

        // Assert
        failures.ShouldNotContain(f => f.Contains("Seed:DefaultAdminPassword", StringComparison.Ordinal));
        failures.ShouldNotContain(f => f.Contains("MailOptions", StringComparison.Ordinal));
    }

    [Fact]
    public void Looks_Should_AcceptARealSecret_When_ItCarriesNoPlaceholderMarker()
    {
        // Act + Assert
        PlaceholderSecret.Looks("8Kq2f1nT0xVb9aMw3hLp6ZcR5yGdE7jU4sNiOo1v").ShouldBeFalse();
        PlaceholderSecret.Looks("   ").ShouldBeTrue();
        PlaceholderSecret.Looks("replace-with-your-own-key").ShouldBeTrue();
    }

    #endregion
}
