using System.Diagnostics.Metrics;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Boilerplate.Modules.Identity;
using Boilerplate.Modules.Identity.Authorization.Jwt;
using Boilerplate.Modules.Identity.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;

namespace Identity.Tests.Services;

/// <summary>
/// Tests for TokenService — it issues JWT access tokens only; refresh tokens are session rows
/// owned by <c>ISessionService</c> (ADR-0002).
/// </summary>
public sealed class TokenServiceTests : IDisposable
{
    private const string SigningKey = "this-is-a-very-long-signing-key-32!!";
    private const string Issuer = "boilerplate-issuer";
    private const string Audience = "boilerplate-audience";

    private static readonly DateTimeOffset FixedNow =
        new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    private readonly ILogger<TokenService> _logger;
    private readonly IdentityMetrics _metrics;

    public TokenServiceTests()
    {
        _logger = Substitute.For<ILogger<TokenService>>();

        // IdentityMetrics only needs a Meter; return a real one from a stubbed factory.
        var meterFactory = Substitute.For<IMeterFactory>();
        meterFactory.Create(Arg.Any<MeterOptions>()).Returns(_ => new Meter(IdentityMetrics.MeterName));
        _metrics = new IdentityMetrics(meterFactory);
    }

    private TokenService CreateService(
        int accessTokenMinutes = 30,
        int refreshTokenDays = 7)
    {
        var options = Options.Create(new JwtOptions
        {
            Issuer = Issuer,
            Audience = Audience,
            SigningKey = SigningKey,
            AccessTokenMinutes = accessTokenMinutes,
            RefreshTokenDays = refreshTokenDays
        });

        var timeProvider = new FixedTimeProvider(FixedNow);
        return new TokenService(options, _logger, _metrics, timeProvider);
    }

    private static Claim[] SampleClaims() =>
        new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "user-123"),
            new Claim(ClaimTypes.Email, "user@example.com")
        };

    private static JwtSecurityToken ReadToken(string token) =>
        new JwtSecurityTokenHandler().ReadJwtToken(token);

    #region IssueAccessOnlyAsync Tests

    [Fact]
    public async Task IssueAccessOnlyAsync_Should_ProduceTokenWithIssuerAudienceAndClaims()
    {
        // Arrange
        var service = CreateService();

        // Act
        var (accessToken, _) = await service.IssueAccessOnlyAsync("user-123", SampleClaims());
        var jwt = ReadToken(accessToken);

        // Assert
        jwt.Issuer.ShouldBe(Issuer);
        jwt.Audiences.ShouldContain(Audience);
        jwt.Claims.ShouldContain(c => c.Type == ClaimTypes.NameIdentifier && c.Value == "user-123");
        jwt.Claims.ShouldContain(c => c.Type == ClaimTypes.Email && c.Value == "user@example.com");
    }

    [Fact]
    public async Task IssueAccessOnlyAsync_Should_StampIssuedAtAndNotBefore_When_TokenIsMinted()
    {
        // Arrange
        var service = CreateService();

        // Act
        var (accessToken, _) = await service.IssueAccessOnlyAsync("user-123", SampleClaims(), TimeSpan.FromMinutes(5));
        var jwt = ReadToken(accessToken);

        // Assert — `iat` dates the token, `nbf` stops it being valid before it was minted.
        var issuedAt = jwt.Claims.Where(c => c.Type == JwtRegisteredClaimNames.Iat).ToList();
        issuedAt.Count.ShouldBe(1, "exactly one iat claim must be emitted");
        issuedAt[0].Value.ShouldBe(EpochTime.GetIntDate(FixedNow.UtcDateTime).ToString(CultureInfo.InvariantCulture));

        jwt.ValidFrom.ShouldBe(FixedNow.UtcDateTime);
        jwt.ValidTo.ShouldBe(FixedNow.UtcDateTime.AddMinutes(5));
    }

    [Fact]
    public async Task IssueAccessOnlyAsync_Should_ProduceTokenSignedWithConfiguredKey()
    {
        // Arrange - use the system clock so the issued token is valid against full validation (incl. lifetime)
        var options = Options.Create(new JwtOptions
        {
            Issuer = Issuer,
            Audience = Audience,
            SigningKey = SigningKey,
            AccessTokenMinutes = 30,
            RefreshTokenDays = 7
        });
        var service = new TokenService(options, _logger, _metrics, TimeProvider.System);

        // Act
        var (accessToken, _) = await service.IssueAccessOnlyAsync("user-123", SampleClaims());

        // Assert - signature, issuer and audience must validate against the configured signing key
        var handler = new JwtSecurityTokenHandler();
        var validationParameters = new TokenValidationParameters
        {
            ValidIssuer = Issuer,
            ValidAudience = Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)),
            ClockSkew = TimeSpan.FromMinutes(5)
        };

        var validationResult = await handler.ValidateTokenAsync(accessToken, validationParameters);

        validationResult.IsValid.ShouldBeTrue();
    }

    [Fact]
    public async Task IssueAccessOnlyAsync_Should_UseConfiguredLifetime_When_NoOverride()
    {
        // Arrange
        var service = CreateService(accessTokenMinutes: 30);

        // Act
        var (accessToken, expiresAt) = await service.IssueAccessOnlyAsync("user-123", SampleClaims());

        // Assert
        accessToken.ShouldNotBeNullOrWhiteSpace();
        expiresAt.ShouldBe(FixedNow.UtcDateTime.AddMinutes(30));
    }

    [Fact]
    public async Task IssueAccessOnlyAsync_Should_UseSuppliedLifetime_When_Overridden()
    {
        // Arrange - caller-supplied lifetime wins over configured AccessTokenMinutes
        var service = CreateService(accessTokenMinutes: 30);
        var lifetime = TimeSpan.FromMinutes(2);

        // Act
        var (_, expiresAt) = await service.IssueAccessOnlyAsync("user-123", SampleClaims(), lifetime);

        // Assert
        expiresAt.ShouldBe(FixedNow.UtcDateTime.Add(lifetime));
    }

    [Fact]
    public async Task IssueAccessOnlyAsync_Should_EmbedSuppliedClaims()
    {
        // Arrange
        var service = CreateService();

        // Act
        var (accessToken, _) = await service.IssueAccessOnlyAsync("user-123", SampleClaims());
        var jwt = ReadToken(accessToken);

        // Assert
        jwt.Claims.ShouldContain(c => c.Type == ClaimTypes.NameIdentifier && c.Value == "user-123");
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_Should_Throw_When_OptionsNull()
    {
        // Act & Assert
        Should.Throw<ArgumentNullException>(() =>
            new TokenService(null!, _logger, _metrics, TimeProvider.System));
    }

    #endregion

    public void Dispose() => _metrics.Dispose();

    /// <summary>
    /// Minimal TimeProvider that always reports a fixed instant, so token expiry math is deterministic.
    /// </summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
