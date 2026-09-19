using Boilerplate.BuildingBlocks.Web;
using Microsoft.Extensions.Hosting;

namespace Framework.Tests.Web;

/// <summary>
/// Modules only receive the IHostApplicationBuilder, so the platform flags travel in its Properties
/// bag. The contract that matters: a module that asks without a host having called AddHeroPlatform
/// must see the defaults, never a null or a disabled feature.
/// </summary>
public sealed class AppPlatformOptionsExtensionsTests
{
    [Fact]
    public void GetHeroPlatformOptions_Should_Return_Defaults_When_Nothing_Was_Published()
    {
        // Arrange — a bare host, as in a test or a tool that wires a module directly.
        IHostApplicationBuilder builder = Host.CreateApplicationBuilder();

        // Act
        var options = builder.GetHeroPlatformOptions();

        // Assert — defaults leave every feature as it behaved before the flag existed.
        options.ShouldNotBeNull();
        options.EnableAuthentication.ShouldBeTrue();
    }

    [Fact]
    public void GetHeroPlatformOptions_Should_Return_What_The_Host_Published()
    {
        // Arrange
        IHostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.SetHeroPlatformOptions(new AppPlatformOptions { EnableAuthentication = false });

        // Act
        var options = builder.GetHeroPlatformOptions();

        // Assert
        options.EnableAuthentication.ShouldBeFalse();
    }
}
