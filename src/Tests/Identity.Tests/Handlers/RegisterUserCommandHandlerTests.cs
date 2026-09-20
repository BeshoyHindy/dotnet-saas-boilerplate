using AutoFixture;
using Boilerplate.BuildingBlocks.Web.Origin;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Users.RegisterUser;
using Boilerplate.Modules.Identity.Features.v1.Users.RegisterUser;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Identity.Tests.Handlers;

/// <summary>
/// Tests for RegisterUserCommandHandler - handles user registration.
/// The confirmation-link origin comes from configuration, never from the command or the request.
/// </summary>
public sealed class RegisterUserCommandHandlerTests
{
    private const string ConfiguredOrigin = "https://app.example.com/";

    private readonly IUserService _userService;
    private readonly IOptions<OriginOptions> _originOptions;
    private readonly RegisterUserCommandHandler _sut;
    private readonly IFixture _fixture;

    public RegisterUserCommandHandlerTests()
    {
        _userService = Substitute.For<IUserService>();
        _originOptions = Substitute.For<IOptions<OriginOptions>>();
        _originOptions.Value.Returns(new OriginOptions { OriginUrl = new Uri(ConfiguredOrigin) });
        _sut = new RegisterUserCommandHandler(_userService, _originOptions);
        _fixture = new Fixture();
    }

    private static RegisterUserCommand ValidCommand() => new()
    {
        FirstName = "John",
        LastName = "Doe",
        Email = "john.doe@example.com",
        UserName = "johndoe",
        Password = "Password123!",
        ConfirmPassword = "Password123!",
        PhoneNumber = "+1234567890",
    };

    #region Handle - Happy Path Tests

    [Fact]
    public async Task Handle_Should_ReturnRegisteredUserId_When_RegistrationIsSuccessful()
    {
        // Arrange
        var command = ValidCommand();
        var expectedUserId = _fixture.Create<string>();

        _userService.RegisterAsync(
            command.FirstName,
            command.LastName,
            command.Email,
            command.UserName,
            command.Password,
            command.ConfirmPassword,
            command.PhoneNumber!,
            Arg.Any<CancellationToken>())
            .Returns(expectedUserId);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.ShouldNotBeNull();
        result.UserId.ShouldBe(expectedUserId);
    }

    [Fact]
    public async Task Handle_Should_CallUserServiceWithCorrectParameters_When_RegistrationIsRequested()
    {
        // Arrange
        var command = ValidCommand();
        command.FirstName = "Jane";
        command.LastName = "Smith";
        command.Email = "jane.smith@example.com";
        command.UserName = "janesmith";

        var userId = _fixture.Create<string>();
        _userService.RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(userId);

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _userService.Received(1).RegisterAsync(
            command.FirstName,
            command.LastName,
            command.Email,
            command.UserName,
            command.Password,
            command.ConfirmPassword,
            command.PhoneNumber!,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_HandleNullPhoneNumber_When_NotProvided()
    {
        // Arrange
        var command = ValidCommand();
        command.PhoneNumber = null;

        var userId = _fixture.Create<string>();
        _userService.RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(userId);

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _userService.Received(1).RegisterAsync(
            command.FirstName,
            command.LastName,
            command.Email,
            command.UserName,
            command.Password,
            command.ConfirmPassword,
            string.Empty, // Should convert null to empty string
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Should_Register_When_AnOriginIsConfigured()
    {
        // Arrange — the mailed link's base URL is configuration, not anything the caller controls.
        // It no longer travels into registration: the confirmation mail is built by the handler of
        // the registration event (#86), which resolves the same configured origin. What is left here
        // is the precondition — an origin exists, so the sign-up may proceed.
        var command = ValidCommand();
        _userService.RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_fixture.Create<string>());

        // Act
        await _sut.Handle(command, CancellationToken.None);

        // Assert
        await _userService.Received(1).RegisterAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    #endregion

    #region Handle - Exception Tests

    [Fact]
    public async Task Handle_Should_ThrowException_When_UserServiceThrows()
    {
        // Arrange
        var command = ValidCommand();

        var expectedExceptionMessage = "Email already exists";
        _userService.RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException(expectedExceptionMessage));

        // Act & Assert
        var exception = await Should.ThrowAsync<InvalidOperationException>(
            async () => await _sut.Handle(command, CancellationToken.None));

        exception.Message.ShouldBe(expectedExceptionMessage);
    }

    [Fact]
    public async Task Handle_Should_Throw_When_OriginIsNotConfigured()
    {
        // Arrange — registering without a configured origin would mail an unusable link; fail loudly.
        _originOptions.Value.Returns(new OriginOptions { OriginUrl = null });

        // Act & Assert
        await Should.ThrowAsync<InvalidOperationException>(
            async () => await _sut.Handle(ValidCommand(), CancellationToken.None));

        await _userService.DidNotReceive().RegisterAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
    public async Task Handle_Should_PassCancellationToken_ToUserService()
    {
        // Arrange
        var command = ValidCommand();
        var userId = _fixture.Create<string>();
        using var cts = new CancellationTokenSource();
        var cancellationToken = cts.Token;

        _userService.RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), cancellationToken)
            .Returns(userId);

        // Act
        await _sut.Handle(command, cancellationToken);

        // Assert
        await _userService.Received(1).RegisterAsync(
            command.FirstName,
            command.LastName,
            command.Email,
            command.UserName,
            command.Password,
            command.ConfirmPassword,
            command.PhoneNumber!,
            cancellationToken);
    }

    #endregion

    #region Handle - Edge Cases Tests

    [Fact]
    public async Task Handle_Should_HandleEmptyStrings_When_ProvidedInCommand()
    {
        // Arrange
        var command = ValidCommand();
        command.FirstName = "";
        command.LastName = "";
        command.Email = "test@example.com";
        command.UserName = "testuser";
        command.PhoneNumber = "";

        var userId = _fixture.Create<string>();
        _userService.RegisterAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(userId);

        // Act
        var result = await _sut.Handle(command, CancellationToken.None);

        // Assert
        result.UserId.ShouldBe(userId);
        await _userService.Received(1).RegisterAsync("", "", "test@example.com", "testuser", "Password123!", "Password123!", "", Arg.Any<CancellationToken>());
    }

    #endregion
}
