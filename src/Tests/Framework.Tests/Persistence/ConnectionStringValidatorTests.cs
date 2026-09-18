using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.BuildingBlocks.Shared.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Framework.Tests.Persistence;

public sealed class ConnectionStringValidatorTests
{
    private static ConnectionStringValidator Build(string provider)
    {
        var options = Options.Create(new DatabaseOptions { Provider = provider });
        var logger = Substitute.For<ILogger<ConnectionStringValidator>>();
        return new ConnectionStringValidator(options, logger);
    }

    #region Happy Path

    [Fact]
    public void TryValidate_Should_ReturnTrue_When_PostgresConnectionStringValid()
    {
        // Arrange
        var sut = Build(DbProviders.PostgreSQL);

        // Act
        var result = sut.TryValidate("Host=localhost;Port=5432;Database=boilerplate;Username=postgres;Password=pwd");

        // Assert
        result.ShouldBeTrue();
    }

    [Fact]
    public void TryValidate_Should_HonorExplicitProviderOverride_When_ProvidedArgument()
    {
        // Arrange — configured provider is unsupported, but the call passes PostgreSQL explicitly.
        var sut = Build("SQLITE");

        // Act
        var result = sut.TryValidate("Host=localhost;Database=boilerplate;", DbProviders.PostgreSQL);

        // Assert
        result.ShouldBeTrue();
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void TryValidate_Should_ReturnFalse_When_ProviderUnsupported()
    {
        // Arrange — PostgreSQL is the only supported provider; anything else fails closed.
        var sut = Build("SQLITE");

        // Act
        var result = sut.TryValidate("any-string");

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_Should_ReturnFalse_When_PostgresConnectionStringMalformed()
    {
        // Arrange
        var sut = Build(DbProviders.PostgreSQL);

        // Act — unknown keyword triggers ArgumentException in the builder.
        var result = sut.TryValidate("Host=localhost;ThisKeyIsNotValid=oops");

        // Assert
        result.ShouldBeFalse();
    }

    [Fact]
    public void TryValidate_Should_ReturnTrue_When_EmptyOrWhitespace()
    {
        // Arrange — builders accept empty/whitespace as a valid (empty) connection string.
        var sut = Build(DbProviders.PostgreSQL);

        // Act & Assert
        sut.TryValidate(string.Empty).ShouldBeTrue();
        sut.TryValidate("   ").ShouldBeTrue();
    }

    #endregion
}
