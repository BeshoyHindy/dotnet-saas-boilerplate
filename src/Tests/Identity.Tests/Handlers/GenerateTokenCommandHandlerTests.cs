using AutoFixture;
using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Eventing.Outbox;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Auditing.Contracts;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Tokens.TokenGeneration;
using Boilerplate.Modules.Identity.Features.v1.Tokens.TokenGeneration;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Identity.Tests.Handlers;

/// <summary>
/// Tests for GenerateTokenCommandHandler - handles user login and token generation.
/// </summary>
public sealed class GenerateTokenCommandHandlerTests
{
    private readonly IIdentityService _identityService;
    private readonly ITokenService _tokenService;
    private readonly ISecurityAudit _securityAudit;
    private readonly IRequestContext _requestContext;
    private readonly IOutboxStore _outboxStore;
    private readonly IMultiTenantContextAccessor<AppTenantInfo> _multiTenantContextAccessor;
    private readonly ISessionService _sessionService;
    private readonly GenerateTokenCommandHandler _sut;
    private readonly IFixture _fixture;

    public GenerateTokenCommandHandlerTests()
    {
        _identityService = Substitute.For<IIdentityService>();
        _tokenService = Substitute.For<ITokenService>();
        _securityAudit = Substitute.For<ISecurityAudit>();
        _requestContext = Substitute.For<IRequestContext>();
        _outboxStore = Substitute.For<IOutboxStore>();
        _multiTenantContextAccessor = Substitute.For<IMultiTenantContextAccessor<AppTenantInfo>>();
        _sessionService = Substitute.For<ISessionService>();

        _sut = new GenerateTokenCommandHandler(
            _identityService,
            _tokenService,
            _securityAudit,
            _requestContext,
            _outboxStore,
            _multiTenantContextAccessor,
            _sessionService);

        _fixture = new Fixture();
    }

    private SessionTokenDto ArrangeSession(Guid? sessionId = null)
    {
        var session = new SessionTokenDto(
            sessionId ?? Guid.NewGuid(),
            _fixture.Create<string>(),
            DateTime.UtcNow.AddDays(7));

        _sessionService
            .CreateSessionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(session);

        return session;
    }

    private (string AccessToken, DateTime ExpiresAt) ArrangeAccessToken()
    {
        var issued = (_fixture.Create<string>(), DateTime.UtcNow.AddHours(1));
        _tokenService
            .IssueAccessOnlyAsync(Arg.Any<string>(), Arg.Any<IEnumerable<Claim>>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>())
            .Returns(issued);
        return issued;
    }

    #region Handle - Happy Path Tests

    [Fact]
    public async Task Handle_Should_ReturnTokenResponse_When_CredentialsAreValid()
    {
        // Arrange
        var command = new GenerateTokenCommand("user@example.com", "password123");
        var userId = _fixture.Create<string>();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, "TestUser"),
            new(ClaimTypes.Email, command.Email)
        };

        _requestContext.IpAddress.Returns("192.168.1.1");
        _requestContext.UserAgent.Returns("TestAgent");
        _requestContext.ClientId.Returns("test-client");

        _identityService.ValidateCredentialsAsync(command.Email, command.Password, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((userId, claims));

        var session = ArrangeSession();
        var issued = ArrangeAccessToken();

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert — the refresh token and its expiry come from the session, not the token service.
        result.ShouldNotBeNull();
        result.AccessToken.ShouldBe(issued.AccessToken);
        result.RefreshToken.ShouldBe(session.RefreshToken);
        result.RefreshTokenExpiresAt.ShouldBe(session.RefreshTokenExpiresAt);
        result.AccessTokenExpiresAt.ShouldBe(issued.ExpiresAt);
    }

    [Fact]
    public async Task Handle_Should_StampAccessTokenWithTheSessionId()
    {
        // Arrange
        var command = new GenerateTokenCommand("user@example.com", "password123");
        var userId = _fixture.Create<string>();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };

        _requestContext.IpAddress.Returns("192.168.1.1");
        _requestContext.UserAgent.Returns("TestAgent");
        _requestContext.ClientId.Returns("test-client");

        _identityService.ValidateCredentialsAsync(command.Email, command.Password, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((userId, claims));

        var session = ArrangeSession();
        ArrangeAccessToken();

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert — `sid` names the device's session row, so consumers can correlate across rotations.
        await _tokenService.Received(1).IssueAccessOnlyAsync(
            userId,
            Arg.Is<IEnumerable<Claim>>(c => c.Any(x =>
                x.Type == JwtRegisteredClaimNames.Sid && x.Value == session.SessionId.ToString())),
            null,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_CallAllServicesWithCorrectParameters_When_CredentialsAreValid()
    {
        // Arrange
        var command = new GenerateTokenCommand("user@example.com", "password123");
        var userId = _fixture.Create<string>();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, "TestUser")
        };

        _requestContext.IpAddress.Returns("192.168.1.1");
        _requestContext.UserAgent.Returns("TestAgent");
        _requestContext.ClientId.Returns("test-client");

        _identityService.ValidateCredentialsAsync(command.Email, command.Password, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((userId, claims));

        ArrangeSession();
        ArrangeAccessToken();

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _identityService.Received(1).ValidateCredentialsAsync(command.Email, command.Password, Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _sessionService.Received(1).CreateSessionAsync(userId, "192.168.1.1", "TestAgent", Arg.Any<CancellationToken>());
        await _securityAudit.Received(1).LoginSucceededAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _securityAudit.Received(1).TokenIssuedAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
        await _outboxStore.Received(1).AddAsync(Arg.Any<Boilerplate.BuildingBlocks.Eventing.Abstractions.IIntegrationEvent>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region Handle - Invalid Credentials Tests

    [Fact]
    public async Task Handle_Should_ThrowUnauthorizedAccessException_When_CredentialsAreInvalid()
    {
        // Arrange
        var command = new GenerateTokenCommand("user@example.com", "wrongpassword");

        _requestContext.IpAddress.Returns("192.168.1.1");
        _requestContext.ClientId.Returns("test-client");

        _identityService.ValidateCredentialsAsync(command.Email, command.Password, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((ValueTuple<string, IReadOnlyList<Claim>>?)null);

        // Act & Assert
        var exception = await Should.ThrowAsync<UnauthorizedAccessException>(
            async () => await _sut.Handle(command, CancellationToken.None));

        exception.Message.ShouldBe("Invalid credentials.");
    }

    [Fact]
    public async Task Handle_Should_AuditFailedLogin_When_CredentialsAreInvalid()
    {
        // Arrange
        var command = new GenerateTokenCommand("user@example.com", "wrongpassword");

        _requestContext.IpAddress.Returns("192.168.1.1");
        _requestContext.ClientId.Returns("test-client");

        _identityService.ValidateCredentialsAsync(command.Email, command.Password, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((ValueTuple<string, IReadOnlyList<Claim>>?)null);

        // Act
        await Should.ThrowAsync<UnauthorizedAccessException>(
            async () => await _sut.Handle(command, CancellationToken.None));

        // Assert
        await _securityAudit.Received(1).LoginFailedAsync(
            command.Email,
            "test-client",
            "InvalidCredentials",
            "192.168.1.1",
            Arg.Any<CancellationToken>());
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
        var command = new GenerateTokenCommand("user@example.com", "password123");
        var userId = _fixture.Create<string>();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };
        using var cts = new CancellationTokenSource();
        var cancellationToken = cts.Token;

        _requestContext.IpAddress.Returns("192.168.1.1");
        _requestContext.UserAgent.Returns("TestAgent");
        _requestContext.ClientId.Returns("test-client");

        _identityService.ValidateCredentialsAsync(command.Email, command.Password, null, cancellationToken)
            .Returns((userId, claims));

        ArrangeSession();
        ArrangeAccessToken();

        // Act
        await _sut.Handle(command, cancellationToken);

        // Assert
        await _identityService.Received(1).ValidateCredentialsAsync(command.Email, command.Password, null, cancellationToken);
        await _sessionService.Received(1).CreateSessionAsync(userId, "192.168.1.1", "TestAgent", cancellationToken);
        await _tokenService.Received(1).IssueAccessOnlyAsync(userId, Arg.Any<IEnumerable<Claim>>(), null, cancellationToken);
        await _outboxStore.Received(1).AddAsync(Arg.Any<Boilerplate.BuildingBlocks.Eventing.Abstractions.IIntegrationEvent>(), cancellationToken);
    }

    #endregion

    #region Handle - Session Creation Failure Tests

    [Fact]
    public async Task Handle_Should_FailTheLogin_When_SessionCreationFails()
    {
        // Arrange
        var command = new GenerateTokenCommand("user@example.com", "password123");
        var userId = _fixture.Create<string>();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };

        _requestContext.IpAddress.Returns("192.168.1.1");
        _requestContext.UserAgent.Returns("TestAgent");
        _requestContext.ClientId.Returns("test-client");

        _identityService.ValidateCredentialsAsync(command.Email, command.Password, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((userId, claims));

        _sessionService
            .CreateSessionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Database not available"));

        // Act & Assert — a login with no session row can neither refresh nor be revoked, so it
        // must not succeed. The failure propagates instead of being logged and swallowed.
        await Should.ThrowAsync<InvalidOperationException>(
            async () => await _sut.Handle(command, CancellationToken.None));

        await _tokenService.DidNotReceive().IssueAccessOnlyAsync(
            Arg.Any<string>(), Arg.Any<IEnumerable<Claim>>(), Arg.Any<TimeSpan?>(), Arg.Any<CancellationToken>());
    }

    #endregion

    #region Handle - Request Context Tests

    [Fact]
    public async Task Handle_Should_HandleMissingRequestContextValues()
    {
        // Arrange
        var command = new GenerateTokenCommand("user@example.com", "password123");
        var userId = _fixture.Create<string>();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId) };

        // Request context returns null values
        _requestContext.IpAddress.Returns((string?)null);
        _requestContext.UserAgent.Returns((string?)null);
        _requestContext.ClientId.Returns("test-client");

        _identityService.ValidateCredentialsAsync(command.Email, command.Password, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns((userId, claims));

        ArrangeSession();
        ArrangeAccessToken();

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        await _securityAudit.Received().LoginSucceededAsync(
            userId,
            Arg.Any<string>(),
            "test-client",
            "unknown", // IP should default to "unknown"
            "unknown", // UserAgent should default to "unknown"
            Arg.Any<CancellationToken>());
    }

    #endregion
}
