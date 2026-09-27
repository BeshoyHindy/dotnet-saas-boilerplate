using Boilerplate.BuildingBlocks.Web.Cors;
using Microsoft.Extensions.Configuration;

namespace Framework.Tests.Web.Cors;

/// <summary>
/// The configuration binder appends configured array entries to whatever a bound property already
/// holds instead of replacing it (it walks the section's numeric-keyed children and adds each one to
/// the existing collection). <see cref="CorsOptions.AllowedHeaders"/> and
/// <see cref="CorsOptions.AllowedMethods"/> once defaulted to <c>["*"]</c>, so binding the API host's
/// own restricted CORS sections produced <c>["*", "content-type", "authorization"]</c> and a literal
/// <c>*</c> method — the restriction never actually took effect. These tests bind the real shipped
/// appsettings sections and assert the configured lists come through exactly, with no smuggled
/// wildcard.
/// </summary>
public sealed class CorsOptionsBindingTests
{
    private static readonly string ApiDirectory = Path.Combine(FindRepoRoot(), "src", "Host", "Boilerplate.Api");

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "src", "Boilerplate.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root (src/Boilerplate.slnx) not found.");
    }

    private static CorsOptions Bind(params string[] jsonFileNames)
    {
        var builder = new ConfigurationBuilder().SetBasePath(ApiDirectory);
        foreach (var fileName in jsonFileNames)
        {
            builder.AddJsonFile(fileName, optional: false);
        }

        // Get<T>() constructs CorsOptions with its default (property-initializer) values and then
        // binds the section onto it — the same "seed, then bind" path AddOptions<T>().Bind(...) uses
        // in Extensions.AddAppCors, so it reproduces the append bug exactly as the host sees it.
        return builder.Build().GetSection(nameof(CorsOptions)).Get<CorsOptions>()
            ?? throw new InvalidOperationException("CorsOptions section did not bind.");
    }

    #region Exception

    [Fact]
    public void Binding_Should_Not_Smuggle_A_Wildcard_When_AppSettingsJsonRestrictsHeadersAndMethods()
    {
        // Act — appsettings.json alone: AllowAll: false, real origins, real headers and methods.
        var options = Bind("appsettings.json");

        // Assert
        options.AllowedHeaders.ShouldNotContain("*");
        options.AllowedMethods.ShouldNotContain("*");
        options.AllowedHeaders.ShouldBe(["content-type", "authorization"]);
        options.AllowedMethods.ShouldBe(["GET", "POST", "PUT", "PATCH", "DELETE"]);
    }

    [Fact]
    public void Binding_Should_Not_Smuggle_A_Wildcard_When_ProductionAppSettingsRestrictsHeadersAndMethods()
    {
        // Act — appsettings.json + appsettings.Production.json layered exactly as the host layers them.
        var options = Bind("appsettings.json", "appsettings.Production.json");

        // Assert
        options.AllowedHeaders.ShouldNotContain("*");
        options.AllowedMethods.ShouldNotContain("*");
        options.AllowedHeaders.ShouldBe(["content-type", "authorization"]);
        options.AllowedMethods.ShouldBe(["GET", "POST", "PUT", "PATCH", "DELETE"]);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void Binding_Should_Leave_DevelopmentAllowAll_Unaffected_When_HeadersAndMethodsAreNotConfigured()
    {
        // Act — the Development overlay flips AllowAll on and configures neither header nor method
        // list; AddAppCors' AllowAll branch never reads either one, so an empty default there
        // changes nothing observable for it.
        var options = Bind("appsettings.json", "appsettings.Development.json");

        // Assert
        options.AllowAll.ShouldBeTrue();
    }

    #endregion
}
