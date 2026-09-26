using Boilerplate.BuildingBlocks.Mailing;
using Boilerplate.BuildingBlocks.Mailing.Services;
using Boilerplate.Modules.Identity.Contracts.Events;
using Boilerplate.Modules.Identity.Events;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Identity.Tests.Events;

/// <summary>
/// The welcome mail is sent as text/html, and the first name is caller-supplied on self-registration,
/// so it reaches an HTML parser. It has to arrive encoded, and the message carries a text/plain twin.
/// </summary>
public sealed class UserRegisteredEmailHandlerTests
{
    private readonly IMailService _mailService = Substitute.For<IMailService>();

    private UserRegisteredEmailHandler CreateSut() =>
        new(_mailService, NullLogger<UserRegisteredEmailHandler>.Instance);

    private static UserRegisteredIntegrationEvent EventWithFirstName(string firstName) =>
        new(
            Id: Guid.NewGuid(),
            OccurredOnUtc: DateTime.UtcNow,
            TenantId: "acme",
            CorrelationId: Guid.NewGuid().ToString(),
            Source: "self-registration",
            UserId: Guid.NewGuid().ToString(),
            Email: "new.user@example.com",
            FirstName: firstName,
            LastName: "Doe");

    private MailRequest CaptureSentMail() =>
        (MailRequest)_mailService.ReceivedCalls().Single().GetArguments()[0]!;

    #region Happy Path

    [Fact]
    public async Task HandleAsync_Should_EncodeTheFirstName_When_ItContainsAScriptTag()
    {
        // Arrange — unencoded, this closes the surrounding element and injects a tag into the mail.
        var sut = CreateSut();

        // Act
        await sut.HandleAsync(EventWithFirstName("<script>alert(1)</script>"), CancellationToken.None);

        // Assert
        var body = CaptureSentMail().Body.ShouldNotBeNull();
        body.ShouldNotContain("<script>");
        body.ShouldContain("Hi &lt;script&gt;alert(1)&lt;/script&gt;, thanks for registering.");
    }

    [Fact]
    public async Task HandleAsync_Should_SendATextAlternative_When_TheEventCarriesAnEmail()
    {
        // Arrange
        var sut = CreateSut();

        // Act
        await sut.HandleAsync(EventWithFirstName("Ada"), CancellationToken.None);

        // Assert — the text part is the sentence verbatim; the HTML part is a real document.
        var mail = CaptureSentMail();
        mail.Subject.ShouldBe("Welcome!");
        mail.TextBody.ShouldBe("Hi Ada, thanks for registering.");
        mail.Body.ShouldNotBeNull().ShouldStartWith("<!DOCTYPE html>");
        mail.Body.ShouldContain("Hi Ada, thanks for registering.");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task HandleAsync_Should_NotSend_When_TheEventCarriesNoEmail()
    {
        // Arrange
        var sut = CreateSut();
        var @event = EventWithFirstName("Ada") with { Email = string.Empty };

        // Act
        await sut.HandleAsync(@event, CancellationToken.None);

        // Assert
        await _mailService.DidNotReceive().SendAsync(Arg.Any<MailRequest>(), Arg.Any<CancellationToken>());
    }

    #endregion
}
