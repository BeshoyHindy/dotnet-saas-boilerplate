using Boilerplate.BuildingBlocks.Web;
using Microsoft.Extensions.Hosting;

namespace Framework.Tests.Web;

/// <summary>
/// Modules only receive the IHostApplicationBuilder, so the platform flags travel in its Properties
/// bag. The contract that matters: a module that asks without a host having called AddAppPlatform
/// must see the defaults, never a null or a disabled feature.
/// </summary>
public sealed class AppPlatformOptionsExtensionsTests
{
    [Fact]
    public void GetAppPlatformOptions_Should_Return_Defaults_When_Nothing_Was_Published()
    {
        // Arrange — a bare host, as in a test or a tool that wires a module directly.
        IHostApplicationBuilder builder = Host.CreateApplicationBuilder();

        // Act
        var options = builder.GetAppPlatformOptions();

        // Assert — defaults leave every feature as it behaved before the flag existed.
        options.ShouldNotBeNull();
        options.EnableAuthentication.ShouldBeTrue();
    }

    [Fact]
    public void GetAppPlatformOptions_Should_Return_What_The_Host_Published()
    {
        // Arrange
        IHostApplicationBuilder builder = Host.CreateApplicationBuilder();
        builder.SetAppPlatformOptions(new AppPlatformOptions { EnableAuthentication = false });

        // Act
        var options = builder.GetAppPlatformOptions();

        // Assert
        options.EnableAuthentication.ShouldBeFalse();
    }
}
