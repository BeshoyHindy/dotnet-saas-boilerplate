using Boilerplate.BuildingBlocks.Mailing;
using Boilerplate.BuildingBlocks.Mailing.Services;
using MimeKit;

namespace Framework.Tests.Mailing;

// SMTP is the default provider (UseSendGrid defaults to false). The transport needs a server and is
// not where the bodies are mapped; the MIME shape is, so these drive the real builder.
public sealed class SmtpMailServiceTests
{
    private static async Task<MimeMessage> BuildAsync(MailRequest request)
    {
        var email = new MimeMessage();
        await SmtpMailService.AddAttachmentsAsync(email, request, CancellationToken.None);
        return email;
    }

    #region Happy Path

    [Fact]
    public async Task Body_Should_BeMultipartAlternative_When_TheCallerSuppliesText()
    {
        // Arrange
        var request = new MailRequest(
            to: ["dest@x.com"],
            subject: "hi",
            body: "<p>rich</p>",
            textBody: "plain");

        // Act
        using var email = await BuildAsync(request);

        // Assert — a text-only client reads the plain part.
        email.HtmlBody.ShouldBe("<p>rich</p>");
        email.TextBody.ShouldBe("plain");
        email.Body.ShouldNotBeNull().ContentType.MimeType.ShouldBe("multipart/alternative");
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task Body_Should_StayHtmlOnly_When_TheCallerSuppliesNoText()
    {
        // A caller that passes only Body keeps today's single text/html part; no plain part is invented
        // from the markup.
        var request = new MailRequest(to: ["dest@x.com"], subject: "hi", body: "<p>rich</p>");

        using var email = await BuildAsync(request);

        email.HtmlBody.ShouldBe("<p>rich</p>");
        email.TextBody.ShouldBeNull();
        email.Body.ShouldNotBeNull().ContentType.MimeType.ShouldBe("text/html");
    }

    [Fact]
    public async Task Body_Should_KeepBothParts_When_AnAttachmentIsPresent()
    {
        // The attachment wraps the alternative in multipart/mixed. Appending it to the wrong part would
        // lose the plain text and still "have" an attachment.
        var request = new MailRequest(
            to: ["dest@x.com"],
            subject: "hi",
            body: "<p>rich</p>",
            textBody: "plain",
            attachmentData: new Dictionary<string, byte[]> { ["report.pdf"] = [1, 2, 3] });

        using var email = await BuildAsync(request);

        email.Body.ShouldNotBeNull().ContentType.MimeType.ShouldBe("multipart/mixed");
        email.HtmlBody.ShouldBe("<p>rich</p>");
        email.TextBody.ShouldBe("plain");
        email.Attachments.Single().ContentDisposition.ShouldNotBeNull().FileName.ShouldBe("report.pdf");
    }

    #endregion
}
