using MailKit.Security;

namespace Boilerplate.BuildingBlocks.Mailing;

public sealed class MailOptions
{
    public bool UseSendGrid { get; set; }
    public string? From { get; set; }
    public string? DisplayName { get; set; }
    public SmtpOptions? Smtp { get; set; }
    public SendGridOptions? SendGrid { get; set; }
}

public sealed class SmtpOptions
{
    public string? Host { get; set; }
    public int Port { get; set; }
    public string? UserName { get; set; }
    public string? Password { get; set; }

    /// <summary>
    /// How the connection is secured. Defaults to <see cref="SecureSocketOptions.StartTls"/>, which
    /// REQUIRES the server to advertise STARTTLS and fails the send otherwise — the right default for
    /// a real relay. Set it to <see cref="SecureSocketOptions.None"/> only for a local mail catcher
    /// (Mailpit, MailHog) that speaks plain SMTP on a private network.
    /// </summary>
    public SecureSocketOptions SecureSocket { get; set; } = SecureSocketOptions.StartTls;
}

public sealed class SendGridOptions
{
    public string? ApiKey { get; set; }
    public string? From { get; set; }
    public string? DisplayName { get; set; }
}