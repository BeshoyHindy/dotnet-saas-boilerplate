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
            ["Storage:Provider"] = "s3",
            ["Storage:S3:Bucket"] = "boilerplate",
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

    [Theory]
    [InlineData("local")]
    [InlineData("Local")]
    [InlineData("")]
    public void Inspect_Should_RejectLocalStorage_When_ProductionHasNotOptedIn(string provider)
    {
        // Act — Local serves every object anonymously out of wwwroot, publishing private files.
        var failures = ProductionConfigurationGuard.Inspect(Config(("Storage:Provider", provider)));

        // Assert
        failures.ShouldContain(f =>
            f.Contains("Storage:Provider", StringComparison.Ordinal) &&
            f.Contains("Storage:AllowLocalProviderInProduction", StringComparison.Ordinal));
    }

    [Fact]
    public void Inspect_Should_AllowLocalStorage_When_TheDeploymentOptsInExplicitly()
    {
        // Act
        var failures = ProductionConfigurationGuard.Inspect(Config(
            ("Storage:Provider", "local"),
            ("Storage:AllowLocalProviderInProduction", "true")));

        // Assert — an explicit opt-in is the only way past the rule.
        failures.ShouldBeEmpty();
    }

    [Fact]
    public void Inspect_Should_ReportMissingBucket_When_ProviderIsS3()
    {
        // Act
        var failures = ProductionConfigurationGuard.Inspect(Config(("Storage:S3:Bucket", "")));

        // Assert
        failures.ShouldContain(f => f.Contains("Storage:S3:Bucket", StringComparison.Ordinal));
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Inspect_Should_NotRequireABucket_When_LocalStorageIsOptedInto()
    {
        // Act — the opted-in Local deployment has no object store to name.
        var failures = ProductionConfigurationGuard.Inspect(Config(
            ("Storage:Provider", "local"),
            ("Storage:AllowLocalProviderInProduction", "true"),
            ("Storage:S3:Bucket", "")));

        // Assert
        failures.ShouldBeEmpty();
    }

    [Fact]
    public void Inspect_Should_RejectLocalStorage_When_TheOptInIsNotABoolean()
    {
        // Act — "yes" is not an opt-in; only a parseable true opens the door.
        var failures = ProductionConfigurationGuard.Inspect(Config(
            ("Storage:Provider", "local"),
            ("Storage:AllowLocalProviderInProduction", "yes")));

        // Assert
        failures.ShouldContain(f => f.Contains("Storage:Provider", StringComparison.Ordinal));
    }


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

    [Theory]
    // Short words appear inside random base64 runs all the time; flagging those would train
    // operators to work around the check instead of fixing the secret.
    [InlineData("Xxx7QfNbSampleTodoZk1nR4wYbGdE7jU4sNiOo1vSecretPasswordQ")]
    [InlineData("k3xxxV9sampleR2todoP7secretL5")]
    [InlineData("ZXhhbXBsZXNlY3JldHBhc3N3b3JkVE9ETw==")]
    public void Looks_Should_AcceptAKey_When_AShortMarkerIsOnlyARandomFragment(string signingKey)
    {
        // Act + Assert
        PlaceholderSecret.Looks(signingKey).ShouldBeFalse();
    }

    [Theory]
    // …but the same words standing on their own, or delimited, are what people actually type.
    [InlineData("secret")]
    [InlineData("my-secret-key")]
    [InlineData("todo")]
    [InlineData("sample_signing_key")]
    [InlineData("Password123!")]
    [InlineData("xxx")]
    [InlineData("integration-test-signing-key")]
    public void Looks_Should_RejectAKey_When_AShortMarkerStandsAsAWholeWord(string signingKey)
    {
        // Act + Assert
        PlaceholderSecret.Looks(signingKey).ShouldBeTrue();
    }

    #endregion
}
