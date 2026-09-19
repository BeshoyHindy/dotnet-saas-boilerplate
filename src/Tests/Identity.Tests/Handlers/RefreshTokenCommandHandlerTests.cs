using AutoFixture;
using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.Modules.Auditing.Contracts;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Tokens.RefreshToken;
using Boilerplate.Modules.Identity.Features.v1.Tokens.RefreshToken;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Boilerplate.BuildingBlocks.Core.Exceptions;

namespace Identity.Tests.Handlers;

/// <summary>
/// Tests for RefreshTokenCommandHandler — the rotation in the session store is the authority;
/// the handler turns its outcome into tokens or a uniform 401.
/// </summary>
public sealed class RefreshTokenCommandHandlerTests
{
    private readonly IIdentityService _identityService;
    private readonly ITokenService _tokenService;
    private readonly ISecurityAudit _securityAudit;
    private readonly IRequestContext _requestContext;
    private readonly ISessionService _sessionService;
    private readonly RefreshTokenCommandHandler _sut;
    private readonly IFixture _fixture;

    public RefreshTokenCommandHandlerTests()
    {
        _identityService = Substitute.For<IIdentityService>();
        _tokenService = Substitute.For<ITokenService>();
        _securityAudit = Substitute.For<ISecurityAudit>();
        _requestContext = Substitute.For<IRequestContext>();
        _sessionService = Substitute.For<ISessionService>();

        _sut = new RefreshTokenCommandHandler(
            _identityService,
            _tokenService,
            _securityAudit,
            _requestContext,
            _sessionService,
            Substitute.For<ILogger<RefreshTokenCommandHandler>>());

        _fixture = new Fixture();
    }

    private SessionRotationDto ArrangeRotation(string userId, Guid? sessionId = null, CancellationToken ct = default)
    {
        var rotation = new SessionRotationDto(
            SessionRotationStatus.Rotated,
            sessionId ?? Guid.NewGuid(),
            userId,
            _fixture.Create<string>(),
            DateTime.UtcNow.AddDays(7));

        _sessionService.RotateRefreshTokenAsync(Arg.Any<string>(), ct == default ? Arg.Any<CancellationToken>() : ct)
            .Returns(rotation);

        return rotation;
    }

    private (string AccessToken, DateTime ExpiresAt) ArrangeAccessToken(CancellationToken ct = default)
    {
        var issued = (_fixture.Create<string>(), DateTime.UtcNow.AddHours(1));
        _tokenService.IssueAccessOnlyAsync(
                Arg.Any<string>(),
                Arg.Any<IEnumerable<Claim>>(),
                Arg.Any<TimeSpan?>(),
                ct == default ? Arg.Any<CancellationToken>() : ct)
            .Returns(issued);
        return issued;
    }

    #region Handle - Happy Path Tests

    [Fact]
    public async Task Handle_Should_ReturnNewTokens_When_RefreshTokenIsValid()
    {
        // Arrange
        const string userId = "user123";
        var oldAccessToken = CreateValidJwtToken(userId, "test@example.com");
        var command = new RefreshTokenCommand(oldAccessToken, "root.valid-refresh-token");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, "TestUser"),
            new(ClaimTypes.Email, "test@example.com")
        };

        _requestContext.ClientId.Returns("test-client");

        var rotation = ArrangeRotation(userId);
        _identityService.BuildClaimsForRefreshAsync(userId, Arg.Any<CancellationToken>())
            .Returns((userId, claims));
        var issued = ArrangeAccessToken();

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.Token.ShouldBe(issued.AccessToken);
        result.RefreshToken.ShouldBe(rotation.RefreshToken);
        result.RefreshTokenExpiryTime.ShouldBe(rotation.RefreshTokenExpiresAt);
    }

    [Fact]
    public async Task Handle_Should_KeepTheSessionIdInTheSidClaim_When_TokenIsRotated()
    {
        // Arrange
        var userId = _fixture.Create<string>();
        var command = new RefreshTokenCommand("access-token", "root.refresh-token");
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };

        _requestContext.ClientId.Returns("test-client");

        var rotation = ArrangeRotation(userId);
        _identityService.BuildClaimsForRefreshAsync(userId, Arg.Any<CancellationToken>())
            .Returns((userId, claims));
        ArrangeAccessToken();

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert — rotation replaces the token but never the session, so `sid` is stable.
        await _tokenService.Received(1).IssueAccessOnlyAsync(
            userId,
            Arg.Is<IEnumerable<Claim>>(c => c.Any(x =>
                x.Type == JwtRegisteredClaimNames.Sid && x.Value == rotation.SessionId.ToString())),
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_CallAllServicesWithCorrectParameters_When_RefreshTokenIsValid()
    {
        // Arrange
        var command = new RefreshTokenCommand("access-token", "root.refresh-token");
        var userId = _fixture.Create<string>();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };

        _requestContext.ClientId.Returns("test-client");

        ArrangeRotation(userId);
        _identityService.BuildClaimsForRefreshAsync(userId, Arg.Any<CancellationToken>())
            .Returns((userId, claims));
        var issued = ArrangeAccessToken();

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _sessionService.Received(1).RotateRefreshTokenAsync(command.RefreshToken, Arg.Any<CancellationToken>());
        await _identityService.Received(1).BuildClaimsForRefreshAsync(userId, Arg.Any<CancellationToken>());
        await _securityAudit.Received(1).TokenRevokedAsync(userId, "test-client", "RefreshTokenRotated", Arg.Any<CancellationToken>());
        await _securityAudit.Received(1).TokenIssuedAsync(userId, Arg.Any<string>(), "test-client", Arg.Any<string>(), issued.ExpiresAt, Arg.Any<CancellationToken>());
    }

    #endregion

    #region Handle - Rotation Failure Tests

    [Theory]
    [InlineData(SessionRotationStatus.NotFound, "InvalidRefreshToken")]
    [InlineData(SessionRotationStatus.Reused, "RefreshTokenReuseDetected")]
    [InlineData(SessionRotationStatus.Revoked, "SessionRevoked")]
    [InlineData(SessionRotationStatus.Expired, "RefreshTokenExpired")]
    [InlineData(SessionRotationStatus.SecurityStampChanged, "SecurityStampChanged")]
    [InlineData(SessionRotationStatus.Superseded, "RefreshTokenSuperseded")]
    public async Task Handle_Should_Throw_And_AuditTheReason_When_RotationFails(
        SessionRotationStatus status, string expectedReason)
    {
        // Arrange
        var command = new RefreshTokenCommand("access-token", "root.refresh-token");
        var userId = _fixture.Create<string>();

        _requestContext.ClientId.Returns("test-client");

        _sessionService.RotateRefreshTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SessionRotationDto.Failed(status, Guid.NewGuid(), userId));

        // Act & Assert — one message for every failure mode, so the caller learns nothing extra.
        var exception = await Should.ThrowAsync<UnauthorizedException>(
            async () => await _sut.Handle(command, CancellationToken.None));

        exception.Message.ShouldBe("Invalid refresh token.");
        await _securityAudit.Received(1).TokenRevokedAsync(userId, "test-client", expectedReason, Arg.Any<CancellationToken>());
        await _tokenService.DidNotReceive().IssueAccessOnlyAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<Claim>>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_AuditWithUnknownSubject_When_TokenMatchesNoSession()
    {
        // Arrange
        var command = new RefreshTokenCommand("access-token", "root.invalid-refresh-token");

        _requestContext.ClientId.Returns("test-client");

        _sessionService.RotateRefreshTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SessionRotationDto.Failed(SessionRotationStatus.NotFound));

        // Act
        await Should.ThrowAsync<UnauthorizedException>(
            async () => await _sut.Handle(command, CancellationToken.None));

        // Assert
        await _securityAudit.Received(1).TokenRevokedAsync("unknown", "test-client", "InvalidRefreshToken", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_Throw_When_UserCannotBeResolvedAfterRotation()
    {
        // Arrange
        var command = new RefreshTokenCommand("access-token", "root.refresh-token");
        var userId = _fixture.Create<string>();

        _requestContext.ClientId.Returns("test-client");

        ArrangeRotation(userId);
        _identityService.BuildClaimsForRefreshAsync(userId, Arg.Any<CancellationToken>())
            .Returns((ValueTuple<string, IEnumerable<Claim>>?)null);

        // Act & Assert
        var exception = await Should.ThrowAsync<UnauthorizedException>(
            async () => await _sut.Handle(command, CancellationToken.None));

        exception.Message.ShouldBe("Invalid refresh token.");
        await _securityAudit.Received(1).TokenRevokedAsync(userId, "test-client", "UserNotFound", Arg.Any<CancellationToken>());
    }

    #endregion

    #region Handle - Access Token Subject Mismatch Tests

    [Fact]
    public async Task Handle_Should_ThrowUnauthorizedException_When_AccessTokenSubjectMismatch()
    {
        // Arrange
        var wrongAccessToken = CreateValidJwtToken("different-user", "other@example.com");
        var command = new RefreshTokenCommand(wrongAccessToken, "root.valid-refresh-token");
        const string userId = "original-user";
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };

        _requestContext.ClientId.Returns("test-client");

        ArrangeRotation(userId);
        _identityService.BuildClaimsForRefreshAsync(userId, Arg.Any<CancellationToken>())
            .Returns((userId, claims));

        // Act & Assert
        var exception = await Should.ThrowAsync<UnauthorizedException>(
            async () => await _sut.Handle(command, CancellationToken.None));

        exception.Message.ShouldBe("Access token subject mismatch.");
    }

    #endregion

    #region Handle - Null Command Tests

    [Fact]
    public async Task Handle_Should_ThrowArgumentNullException_When_CommandIsNull()
    {
        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await _sut.Handle(null!, CancellationToken.None));
    }

    #endregion

    #region Handle - CancellationToken Tests

    [Fact]
    public async Task Handle_Should_PassCancellationToken_ToAllServices()
    {
        // Arrange
        var command = new RefreshTokenCommand("access-token", "root.refresh-token");
        var userId = _fixture.Create<string>();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        using var cts = new CancellationTokenSource();
        var cancellationToken = cts.Token;

        _requestContext.ClientId.Returns("test-client");

        ArrangeRotation(userId, ct: cancellationToken);
        _identityService.BuildClaimsForRefreshAsync(userId, cancellationToken)
            .Returns((userId, claims));
        ArrangeAccessToken(cancellationToken);

        // Act
        await _sut.Handle(command, cancellationToken);

        // Assert
        await _sessionService.Received(1).RotateRefreshTokenAsync(command.RefreshToken, cancellationToken);
        await _identityService.Received(1).BuildClaimsForRefreshAsync(userId, cancellationToken);
        await _tokenService.Received(1).IssueAccessOnlyAsync(userId, Arg.Any<IEnumerable<Claim>>(), null, cancellationToken);
    }

    #endregion

    #region Helper Methods

    private static string CreateValidJwtToken(string userId, string email)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes("test-signing-key-that-is-at-least-32-bytes-long"));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId),
            new Claim(ClaimTypes.Email, email)
        };
        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    #endregion
}
