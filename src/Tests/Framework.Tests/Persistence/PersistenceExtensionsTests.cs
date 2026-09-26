using Boilerplate.BuildingBlocks.Persistence;
using Boilerplate.BuildingBlocks.Shared.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Framework.Tests.Persistence;

/// <summary>
/// `AddHeroDatabaseOptions` must keep failing a host with an empty connection string at runtime
/// (issue #116): the design-time factories that let `dotnet ef migrations add` skip this validation
/// must not weaken it for the running API.
/// </summary>
public sealed class PersistenceExtensionsTests
{
    private static IConfiguration Config(string connectionString = "")
    {
        var values = new Dictionary<string, string?>
        {
            ["DatabaseOptions:ConnectionString"] = connectionString,
        };
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    #region Exception

    [Fact]
    public void DatabaseOptions_Should_ThrowOptionsValidationException_When_ConnectionStringIsEmpty()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHeroDatabaseOptions(Config());
        var provider = services.BuildServiceProvider();

        // Act
        var act = () => provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

        // Assert
        Should.Throw<OptionsValidationException>(act)
            .Message.ShouldContain("connection string cannot be empty");
    }

    #endregion

    #region Happy Path

    [Fact]
    public void DatabaseOptions_Should_Resolve_When_ConnectionStringIsSet()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddHeroDatabaseOptions(Config("Host=db;Database=app;Username=app;Password=app"));
        var provider = services.BuildServiceProvider();

        // Act
        var options = provider.GetRequiredService<IOptions<DatabaseOptions>>().Value;

        // Assert
        options.ConnectionString.ShouldBe("Host=db;Database=app;Username=app;Password=app");
    }

    #endregion
}
