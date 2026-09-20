using AutoFixture;
using Boilerplate.BuildingBlocks.Web.Origin;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Users.ResendConfirmationEmail;
using Boilerplate.Modules.Identity.Features.v1.Users.ResendConfirmationEmail;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Identity.Tests.Handlers;

/// <summary>
/// Tests for ResendConfirmationEmailCommandHandler. Like registration and password reset, the
/// confirmation link's base URL comes from configuration and never from the request.
/// </summary>
public sealed class ResendConfirmationEmailCommandHandlerTests
{
    private const string ConfiguredOrigin = "https://app.example.com/";

    private readonly IUserService _userService;
    private readonly IOptions<OriginOptions> _originOptions;
    private readonly ResendConfirmationEmailCommandHandler _sut;
    private readonly IFixture _fixture;

    public ResendConfirmationEmailCommandHandlerTests()
    {
        _userService = Substitute.For<IUserService>();
        _originOptions = Substitute.For<IOptions<OriginOptions>>();
        _originOptions.Value.Returns(new OriginOptions { OriginUrl = new Uri(ConfiguredOrigin) });
        _sut = new ResendConfirmationEmailCommandHandler(_userService, _originOptions);
        _fixture = new Fixture();
    }

    #region Happy Path

    [Fact]
    public async Task Handle_Should_ResendWithTheConfiguredOrigin_When_CommandIsValid()
    {
        // Arrange
        var command = _fixture.Create<ResendConfirmationEmailCommand>();

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _userService.Received(1)
            .ResendConfirmationEmailAsync(command.UserId, ConfiguredOrigin, Arg.Any<CancellationToken>());
    }

    #endregion

    #region Exception

    [Fact]
    public async Task Handle_Should_Throw_When_OriginIsNotConfigured()
    {
        // Arrange
        _originOptions.Value.Returns(new OriginOptions { OriginUrl = null });

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(async () =>
            await _sut.Handle(_fixture.Create<ResendConfirmationEmailCommand>(), CancellationToken.None));

        await _userService.DidNotReceive()
            .ResendConfirmationEmailAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_ThrowArgumentNullException_When_CommandIsNull()
    {
        // Act & Assert
        await Should.ThrowAsync<ArgumentNullException>(async () =>
            await _sut.Handle(null!, CancellationToken.None));
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task Handle_Should_PassTheSpecificCancellationToken_ToUserService()
    {
        // Arrange
        var command = _fixture.Create<ResendConfirmationEmailCommand>();
        using var cts = new CancellationTokenSource();

        // Act
        await _sut.Handle(command, cts.Token);

        // Assert
        await _userService.Received(1)
            .ResendConfirmationEmailAsync(command.UserId, ConfiguredOrigin, cts.Token);
    }

    #endregion
}
