using Boilerplate.BuildingBlocks.Web.Cors;
using Boilerplate.BuildingBlocks.Web.Limits;
using Boilerplate.BuildingBlocks.Web.RateLimiting;
using Boilerplate.BuildingBlocks.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Framework.Tests.Web;

/// <summary>
/// Options validators and configurators added for production hardening: request limits, the
/// reverse-proxy forwarded headers, and the environment-aware CORS / rate-limit rules.
/// </summary>
public sealed class HardeningOptionsTests
{
    private static IHostEnvironment Environment(string environmentName)
    {
        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(environmentName);
        return environment;
    }

    #region Request limits

    [Fact]
    public void ConfigureRequestLimits_Should_ApplyBoundedLimits_When_KestrelIsConfigured()
    {
        // Arrange
        var options = new RequestLimitsOptions
        {
            MaxRequestBodyBytes = 2048,
            MaxRequestHeadersTotalBytes = 4096,
            MaxRequestLineBytes = 2048
        };
        var kestrel = new KestrelServerOptions();

        // Act
        new ConfigureRequestLimits(Options.Create(options)).Configure(kestrel);

        // Assert
        kestrel.Limits.MaxRequestBodySize.ShouldBe(2048);
        kestrel.Limits.MaxRequestHeadersTotalSize.ShouldBe(4096);
        kestrel.Limits.MaxRequestLineSize.ShouldBe(2048);
    }

    [Fact]
    public void RequestLimitsOptions_Should_CapBodiesBelowTheKestrelDefault_When_Constructed()
    {
        // Act
        var options = new RequestLimitsOptions();

        // Assert — Kestrel's own default is 30 MB; uploads bypass the API entirely.
        options.MaxRequestBodyBytes.ShouldBe(10 * 1024 * 1024);
    }

    #endregion

    #region Forwarded headers

    [Fact]
    public void ConfigureForwardedHeaders_Should_DoNothing_When_ProxySupportIsDisabled()
    {
        // Arrange
        var forwarded = new ForwardedHeadersOptions();

        // Act
        new ConfigureForwardedHeaders(Options.Create(new ProxyOptions { Enabled = false })).Configure(forwarded);

        // Assert
        forwarded.ForwardedHeaders.ShouldBe(ForwardedHeaders.None);
    }

    [Fact]
    public void ConfigureForwardedHeaders_Should_TrustTheListedProxies_When_Enabled()
    {
        // Arrange
        var proxy = new ProxyOptions
        {
            Enabled = true,
            KnownProxies = ["10.1.2.3"],
            KnownNetworks = ["10.0.0.0/8"],
            ForwardLimit = 2
        };
        var forwarded = new ForwardedHeadersOptions();

        // Act
        new ConfigureForwardedHeaders(Options.Create(proxy)).Configure(forwarded);

        // Assert
        forwarded.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedFor).ShouldBeTrue();
        forwarded.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedProto).ShouldBeTrue();
        forwarded.ForwardedHeaders.HasFlag(ForwardedHeaders.XForwardedHost).ShouldBeTrue();
        forwarded.ForwardLimit.ShouldBe(2);
        forwarded.KnownProxies.Count.ShouldBe(1);
        forwarded.KnownIPNetworks.Count.ShouldBe(1);
    }

    [Fact]
    public void ConfigureForwardedHeaders_Should_ClearTheLoopbackDefaults_When_TrustAnyProxyIsSet()
    {
        // Arrange — the stock defaults trust loopback only, which never matches a proxy container.
        var forwarded = new ForwardedHeadersOptions();

        // Act
        new ConfigureForwardedHeaders(Options.Create(new ProxyOptions { Enabled = true, TrustAnyProxy = true })).Configure(forwarded);

        // Assert
        forwarded.KnownProxies.ShouldBeEmpty();
        forwarded.KnownIPNetworks.ShouldBeEmpty();
    }

    [Fact]
    public void ProxyOptionsValidator_Should_Succeed_When_Disabled()
    {
        // Act
        var result = new ProxyOptionsValidator().Validate(null, new ProxyOptions { Enabled = false });

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void ProxyOptionsValidator_Should_Fail_When_EnabledButNothingIsTrusted()
    {
        // Act
        var result = new ProxyOptionsValidator().Validate(null, new ProxyOptions { Enabled = true });

        // Assert
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("TrustAnyProxy");
    }

    [Theory]
    [InlineData("not-an-ip", "10.0.0.0/8")]
    [InlineData("10.1.2.3", "10.0.0.0")]
    [InlineData("10.1.2.3", "10.0.0.0/64")]
    public void ProxyOptionsValidator_Should_Fail_When_AnAddressDoesNotParse(string knownProxy, string knownNetwork)
    {
        // Arrange
        var options = new ProxyOptions
        {
            Enabled = true,
            KnownProxies = [knownProxy],
            KnownNetworks = [knownNetwork]
        };

        // Act
        var result = new ProxyOptionsValidator().Validate(null, options);

        // Assert
        result.Failed.ShouldBeTrue();
    }

    #endregion

    #region CORS

    [Fact]
    public void CorsOptionsValidator_Should_Fail_When_AllowAllIsUsedInProduction()
    {
        // Act
        var result = new CorsOptionsValidator(Environment(Environments.Production))
            .Validate(null, new CorsOptions { AllowAll = true });

        // Assert
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("AllowAll");
    }

    [Fact]
    public void CorsOptionsValidator_Should_Succeed_When_AllowAllIsUsedInDevelopment()
    {
        // Act
        var result = new CorsOptionsValidator(Environment(Environments.Development))
            .Validate(null, new CorsOptions { AllowAll = true });

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("ftp://example.com")]
    [InlineData("https://example.com/")]
    public void CorsOptionsValidator_Should_Fail_When_AnOriginIsNotABrowserOrigin(string origin)
    {
        // Act
        var result = new CorsOptionsValidator(Environment(Environments.Production))
            .Validate(null, new CorsOptions { AllowAll = false, AllowedOrigins = [origin] });

        // Assert
        result.Failed.ShouldBeTrue();
    }

    [Fact]
    public void CorsOptionsValidator_Should_Succeed_When_OriginsAreAbsoluteAndSlashFree()
    {
        // Act
        var result = new CorsOptionsValidator(Environment(Environments.Production))
            .Validate(null, new CorsOptions { AllowAll = false, AllowedOrigins = ["https://app.example.com"] });

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    #endregion

    #region Rate limiting

    [Fact]
    public void RateLimitingOptionsValidator_Should_Succeed_When_Disabled()
    {
        // Act — the policies are never constructed, so their values cannot break anything.
        var options = new RateLimitingOptions { Enabled = false, Ip = new FixedWindowPolicyOptions { PermitLimit = 0 } };
        var result = new RateLimitingOptionsValidator().Validate(null, options);

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    [Fact]
    public void RateLimitingOptionsValidator_Should_Fail_When_APolicyPermitsNothing()
    {
        // Arrange
        var options = new RateLimitingOptions
        {
            Enabled = true,
            Auth = new FixedWindowPolicyOptions { PermitLimit = 0, WindowSeconds = 60 }
        };

        // Act
        var result = new RateLimitingOptionsValidator().Validate(null, options);

        // Assert
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Auth.PermitLimit");
    }

    [Fact]
    public void RateLimitingOptionsValidator_Should_Fail_When_AWindowIsZero()
    {
        // Arrange
        var options = new RateLimitingOptions
        {
            Enabled = true,
            Ip = new FixedWindowPolicyOptions { PermitLimit = 10, WindowSeconds = 0 }
        };

        // Act
        var result = new RateLimitingOptionsValidator().Validate(null, options);

        // Assert
        result.Failed.ShouldBeTrue();
        result.FailureMessage.ShouldContain("Ip.WindowSeconds");
    }

    [Fact]
    public void RateLimitingOptionsValidator_Should_Succeed_When_TheDefaultsAreUsed()
    {
        // Act
        var result = new RateLimitingOptionsValidator().Validate(null, new RateLimitingOptions());

        // Assert
        result.Succeeded.ShouldBeTrue();
    }

    #endregion
}
