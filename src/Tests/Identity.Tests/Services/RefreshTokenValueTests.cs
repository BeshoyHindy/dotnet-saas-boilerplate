using Boilerplate.Modules.Identity.Features.v1.Tokens;

namespace Identity.Tests.Services;

/// <summary>
/// The refresh token's wire format is a security boundary: the tenant prefix routes the anonymous
/// refresh call and the 32-byte secret is the only thing that authenticates it (ADR-0002).
/// </summary>
public sealed class RefreshTokenValueTests
{
    [Fact]
    public void Issue_Should_PrefixTheTenant_And_AppendBase64UrlSecret()
    {
        var token = RefreshTokenValue.Issue("acme");

        token.ShouldStartWith("acme.");

        var secret = token["acme.".Length..];
        // 32 bytes → 43 base64url characters, unpadded.
        secret.Length.ShouldBe(43);
        secret.ShouldNotContain("+");
        secret.ShouldNotContain("/");
        secret.ShouldNotContain("=");
    }

    [Fact]
    public void Issue_Should_ProduceAUniqueSecretEveryTime()
    {
        var first = RefreshTokenValue.Issue("acme");
        var second = RefreshTokenValue.Issue("acme");

        first.ShouldNotBe(second);
    }

    [Fact]
    public void TryGetTenantId_Should_ReturnTheTenant_When_TokenIsWellFormed()
    {
        var token = RefreshTokenValue.Issue("acme");

        RefreshTokenValue.TryGetTenantId(token, out var tenantId).ShouldBeTrue();
        tenantId.ShouldBe("acme");
    }

    [Fact]
    public void TryGetTenantId_Should_SplitOnTheLastSeparator_When_TenantIdContainsDots()
    {
        // base64url never contains a dot, so the last one is always the separator — which lets a
        // tenant Id keep its own.
        var token = RefreshTokenValue.Issue("acme.eu.west");

        RefreshTokenValue.TryGetTenantId(token, out var tenantId).ShouldBeTrue();
        tenantId.ShouldBe("acme.eu.west");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no-separator")]
    [InlineData(".leading-separator-only")]
    [InlineData("trailing-separator.")]
    public void TryGetTenantId_Should_Fail_When_TokenIsMalformed(string? token)
    {
        RefreshTokenValue.TryGetTenantId(token, out var tenantId).ShouldBeFalse();
        tenantId.ShouldBeNull();
    }

    [Fact]
    public void Hash_Should_BeDeterministic_And_NotContainTheToken()
    {
        var token = RefreshTokenValue.Issue("acme");

        var hash = RefreshTokenValue.Hash(token);

        hash.ShouldBe(RefreshTokenValue.Hash(token));
        hash.ShouldNotContain(token);
        // SHA-256 as base64 is always 44 characters.
        hash.Length.ShouldBe(44);
    }

    [Fact]
    public void Hash_Should_Differ_When_OnlyTheTenantPrefixDiffers()
    {
        var secret = RefreshTokenValue.Issue("acme")["acme.".Length..];

        // The prefix is inside the hashed value, so re-labelling a secret with another tenant's
        // prefix produces a hash that matches nothing.
        RefreshTokenValue.Hash($"acme.{secret}").ShouldNotBe(RefreshTokenValue.Hash($"other.{secret}"));
    }
}
