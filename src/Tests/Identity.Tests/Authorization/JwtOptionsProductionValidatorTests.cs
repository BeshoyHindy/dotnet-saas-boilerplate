using Boilerplate.Modules.Identity.Authorization.Jwt;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Identity.Tests.Authorization;

/// <summary>
/// Environment-aware JwtOptions rules: a signing key that ships in the repository is not a secret,
/// so Production must refuse to boot on one. Development keeps working so the dev loop is unaffected.
/// </summary>
public sealed class JwtOptionsProductionValidatorTests
{
    private static JwtOptionsProductionValidator CreateValidator(string environmentName)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        return new JwtOptionsProductionValidator(environment);
    }

    private static JwtOptions Options(string signingKey, string issuer = "boilerplate", string audience = "boilerplate.clients") =>
        new()
        {
            SigningKey = signingKey,
            Issuer = issuer,
            Audience = audience
        };

    #region Happy Path

    [Fact]
    public void Validate_Should_Succeed_When_ProductionKeyLooksReal()
    {
        // Act
        var result = CreateValidator(Environments.Production)
            .Validate(null, Options("8Kq2f1nT0xVb9aMw3hLp6ZcR5yGdE7jU4sNiOo1v"));

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void Validate_Should_Succeed_When_EnvironmentIsDevelopment()
    {
        // Act — a development key is fine outside Production.
        var result = CreateValidator(Environments.Development)
            .Validate(null, Options("boilerplate-dev-only-do-not-use-in-prod-32+chars-min"));

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    #endregion

    #region Exception

    [Theory]
    [InlineData("boilerplate-dev-only-do-not-use-in-prod-32+chars-min")]
    [InlineData("replace-with-a-real-signing-key-32-chars")]
    [InlineData("changeme-changeme-changeme-changeme")]
    [InlineData("")]
    public void Validate_Should_Fail_When_ProductionKeyIsAPlaceholder(string signingKey)
    {
        // Act
        var result = CreateValidator(Environments.Production).Validate(null, Options(signingKey));

        // Assert
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("SigningKey");
    }

    [Fact]
    public void Validate_Should_Fail_When_ProductionIssuerStillPointsAtLocalhost()
    {
        // Act
        var result = CreateValidator(Environments.Production)
            .Validate(null, Options("8Kq2f1nT0xVb9aMw3hLp6ZcR5yGdE7jU4sNiOo1v", issuer: "https://localhost:7030"));

        // Assert
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("localhost");
    }

    #endregion
}
