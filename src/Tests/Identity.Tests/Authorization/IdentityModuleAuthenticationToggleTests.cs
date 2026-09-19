using Boilerplate.BuildingBlocks.Web;
using Boilerplate.Modules.Identity;
using Boilerplate.Modules.Identity.Authorization.Jwt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Identity.Tests.Authorization;

/// <summary>
/// The DbMigrator loads IdentityModule for its data access but never authenticates a caller and never
/// mints a token. It used to inject a fake "…placeholder…" signing key purely to satisfy
/// JwtOptions.ValidateOnStart(), which the Production validator then rejected — so the migrator could
/// not run in Production at all without being handed the API's real key.
///
/// AppPlatformOptions.EnableAuthentication removes the need for a key entirely. These tests pin both
/// directions: off means a key-less host starts, on means the validation still bites.
/// </summary>
public sealed class IdentityModuleAuthenticationToggleTests
{
    /// <summary>
    /// A Production host configured exactly like the migrator: a usable database, and no JwtOptions
    /// section at all. <paramref name="enableAuthentication"/> is the single variable under test.
    /// </summary>
    private static IHost BuildProductionHost(bool enableAuthentication)
    {
        IHostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production,
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DatabaseOptions:Provider"] = "POSTGRESQL",
            ["DatabaseOptions:ConnectionString"] = "Host=localhost;Database=boilerplate;Username=boilerplate;Password=irrelevant",
            ["DatabaseOptions:MigrationsAssembly"] = "Boilerplate.Migrations.PostgreSQL",
        });

        // Stand in for AddHeroPlatform: publish the one flag IdentityModule reads.
        builder.SetHeroPlatformOptions(new AppPlatformOptions { EnableAuthentication = enableAuthentication });

        new IdentityModule().ConfigureServices(builder);

        return ((HostApplicationBuilder)builder).Build();
    }

    /// <summary>
    /// Runs the same option validation a host runs during StartAsync, without needing a database.
    /// </summary>
    private static void ValidateStartupOptions(IHost host) =>
        host.Services.GetRequiredService<IStartupValidator>().Validate();

    [Fact]
    public void Startup_Should_Succeed_In_Production_Without_JwtOptions_When_AuthenticationDisabled()
    {
        // Arrange — the migrator's configuration: no signing key anywhere.
        using var host = BuildProductionHost(enableAuthentication: false);

        // Act
        var act = () => ValidateStartupOptions(host);

        // Assert — this is the whole point: no key, no failure.
        act.ShouldNotThrow();
    }

    [Fact]
    public void JwtOptions_Should_Not_Be_Validated_When_AuthenticationDisabled()
    {
        // Arrange
        using var host = BuildProductionHost(enableAuthentication: false);

        // Act — the Production validator is registered by ConfigureJwtAuth, which must not have run.
        var validators = host.Services.GetServices<IValidateOptions<JwtOptions>>();

        // Assert
        validators.ShouldBeEmpty();
    }

    [Fact]
    public void Startup_Should_Fail_In_Production_Without_JwtOptions_When_AuthenticationEnabled()
    {
        // Arrange — the API's configuration, minus the signing key it is required to supply.
        using var host = BuildProductionHost(enableAuthentication: true);

        // Act
        var act = () => ValidateStartupOptions(host);

        // Assert — the flag must not have weakened the default: a real API still refuses to boot.
        act.ShouldThrow<OptionsValidationException>();
    }
}
