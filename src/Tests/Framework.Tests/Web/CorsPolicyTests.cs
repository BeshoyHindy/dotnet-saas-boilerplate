using Boilerplate.BuildingBlocks.Web.Cors;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using BoilerplateCorsOptions = Boilerplate.BuildingBlocks.Web.Cors.CorsOptions;
using AspNetCorsOptions = Microsoft.AspNetCore.Cors.Infrastructure.CorsOptions;

namespace Framework.Tests.Web;

/// <summary>
/// The CORS policy <c>AddHeroCors</c> builds, as a browser would meet it. The profile's version
/// travels as <c>ETag</c> out and <c>If-Match</c> back (#107), so a cross-origin client must be able
/// to read the one and send the other in both branches of the policy.
/// </summary>
public sealed class CorsPolicyTests
{
    private static CorsPolicy BuildPolicy(Dictionary<string, string?> settings, string environmentName)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);

        var services = new ServiceCollection();
        services.AddSingleton(environment);
        services.AddHeroCors(configuration);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<AspNetCorsOptions>>().Value.GetPolicy("AppCorsPolicy")!;
    }

    private static Dictionary<string, string?> Restricted(params string[] headers)
    {
        var settings = new Dictionary<string, string?>
        {
            ["CorsOptions:AllowAll"] = "false",
            ["CorsOptions:AllowedOrigins:0"] = "https://app.example.com",
            ["CorsOptions:AllowedMethods:0"] = "PUT",
        };
        for (var i = 0; i < headers.Length; i++)
        {
            settings[$"CorsOptions:AllowedHeaders:{i}"] = headers[i];
        }

        return settings;
    }

    [Fact]
    public void RestrictedPolicy_Should_ExposeETag_AndAllowIfMatch()
    {
        var policy = BuildPolicy(Restricted("content-type", "authorization"), Environments.Production);

        policy.ExposedHeaders.ShouldContain("ETag");
        policy.Headers.ShouldContain(h => string.Equals(h, "if-match", StringComparison.OrdinalIgnoreCase));
        policy.Headers.ShouldContain("content-type");
        policy.Headers.ShouldContain("authorization");
    }

    [Fact]
    public void AllowAllPolicy_Should_ExposeETag()
    {
        var policy = BuildPolicy(
            new Dictionary<string, string?> { ["CorsOptions:AllowAll"] = "true" },
            Environments.Development);

        policy.ExposedHeaders.ShouldContain("ETag");
        policy.AllowAnyHeader.ShouldBeTrue();
    }

    [Fact]
    public void WithIfMatch_Should_KeepASoleWildcardUnchanged()
    {
        // CorsPolicy.AllowAnyHeader is true only while the list is exactly ["*"]; appending to it
        // would turn "any header" into "these two".
        Extensions.WithIfMatch(["*"]).ShouldBe(["*"]);
    }

    [Fact]
    public void WithIfMatch_Should_NotDuplicate_When_AlreadyConfigured()
    {
        Extensions.WithIfMatch(["content-type", "IF-MATCH"]).ShouldBe(["content-type", "IF-MATCH"]);
    }
}
