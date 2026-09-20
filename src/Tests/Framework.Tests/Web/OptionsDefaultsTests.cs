using Boilerplate.BuildingBlocks.Web.Idempotency;
using Boilerplate.BuildingBlocks.Web.RateLimiting;
using Boilerplate.BuildingBlocks.Web.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Framework.Tests.Web;

public sealed class OptionsDefaultsTests
{
    #region SecurityHeadersOptions

    [Fact]
    public void SecurityHeadersOptions_Should_HaveSecureDefaults_When_Constructed()
    {
        // Act
        var options = new SecurityHeadersOptions();

        // Assert
        options.Enabled.ShouldBeTrue();
        options.AllowInlineStyles.ShouldBeTrue();
        options.ExcludedPaths.ShouldBe(["/scalar", "/openapi"]);
        options.ScriptSources.ShouldBeEmpty();
        options.StyleSources.ShouldBeEmpty();
    }

    #endregion

    #region RateLimitingOptions

    [Fact]
    public void RateLimitingOptions_Should_HaveDefaultPolicies_When_Constructed()
    {
        // Act
        var options = new RateLimitingOptions();

        // Assert
        options.Enabled.ShouldBeTrue();
        options.Tenant.PermitLimit.ShouldBe(1000);
        options.User.PermitLimit.ShouldBe(200);
        options.Ip.PermitLimit.ShouldBe(300);
        options.Auth.PermitLimit.ShouldBe(10);
        options.Tenant.WindowSeconds.ShouldBe(60);
        options.Auth.QueueLimit.ShouldBe(0);
    }

    [Fact]
    public void FixedWindowPolicyOptions_Should_HaveDefaults_When_Constructed()
    {
        // Act
        var policy = new FixedWindowPolicyOptions();

        // Assert
        policy.PermitLimit.ShouldBe(100);
        policy.WindowSeconds.ShouldBe(60);
        policy.QueueLimit.ShouldBe(0);
    }

    #endregion

    #region IdempotencyOptions

    [Fact]
    public void IdempotencyOptions_Should_HaveDefaults_When_Constructed()
    {
        // Act
        var options = new IdempotencyOptions();

        // Assert
        options.HeaderName.ShouldBe("Idempotency-Key");
        options.DefaultTtl.ShouldBe(TimeSpan.FromHours(24));
        options.MaxKeyLength.ShouldBe(128);
        options.LockWaitTimeout.ShouldBe(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AddHeroIdempotency_Should_Refuse_A_NonPositive_LockWaitTimeout(int seconds)
    {
        // Not "no lock": SemaphoreSlim reads zero as a poll and a negative as "wait forever", which is
        // the unbounded queue the bound exists to remove. Refuse it where it is configured.
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddHeroIdempotency(configuration);
        services.Configure<IdempotencyOptions>(o => o.LockWaitTimeout = TimeSpan.FromSeconds(seconds));

        using var provider = services.BuildServiceProvider();

        var failure = Should.Throw<OptionsValidationException>(
            () => provider.GetRequiredService<IOptions<IdempotencyOptions>>().Value);
        failure.Message.ShouldContain(nameof(IdempotencyOptions.LockWaitTimeout));
    }

    #endregion
}
