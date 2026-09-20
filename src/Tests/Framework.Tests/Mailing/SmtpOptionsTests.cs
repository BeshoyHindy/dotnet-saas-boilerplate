using Boilerplate.BuildingBlocks.Mailing;
using MailKit.Security;
using Microsoft.Extensions.Configuration;

namespace Framework.Tests.Mailing;

/// <summary>
/// SmtpOptions.SecureSocket exists so a local mail catcher (Mailpit) can be reached over plain SMTP.
/// The risk of a knob like that is silent downgrade, so the default is pinned here: anything that does
/// not explicitly opt out keeps requiring STARTTLS.
/// </summary>
public sealed class SmtpOptionsTests
{
    [Fact]
    public void SecureSocket_Should_Default_To_StartTls()
    {
        // Act
        var options = new SmtpOptions();

        // Assert — a real relay must not be talked to in the clear by accident.
        options.SecureSocket.ShouldBe(SecureSocketOptions.StartTls);
    }

    [Fact]
    public void SecureSocket_Should_Default_To_StartTls_When_Configuration_Omits_It()
    {
        // Arrange — the shape of a normal deployment's config: host and port, nothing about TLS.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MailOptions:Smtp:Host"] = "smtp.example.com",
                ["MailOptions:Smtp:Port"] = "587",
            })
            .Build();

        // Act
        var options = configuration.GetSection("MailOptions").Get<MailOptions>();

        // Assert
        options!.Smtp!.SecureSocket.ShouldBe(SecureSocketOptions.StartTls);
    }

    [Fact]
    public void SecureSocket_Should_Bind_None_For_A_Local_Catcher()
    {
        // Arrange — what docker-compose.yml and the AppHost set for Mailpit.
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MailOptions:Smtp:Host"] = "mailpit",
                ["MailOptions:Smtp:Port"] = "1025",
                ["MailOptions:Smtp:SecureSocket"] = "None",
            })
            .Build();

        // Act
        var options = configuration.GetSection("MailOptions").Get<MailOptions>();

        // Assert
        options!.Smtp!.SecureSocket.ShouldBe(SecureSocketOptions.None);
    }
}
