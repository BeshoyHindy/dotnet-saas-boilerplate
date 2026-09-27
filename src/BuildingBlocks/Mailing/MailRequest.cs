using System.Collections.ObjectModel;

namespace Boilerplate.BuildingBlocks.Mailing;

public class MailRequest(Collection<string> to, string subject, string? body = null, string? from = null, string? displayName = null, string? replyTo = null, string? replyToName = null, Collection<string>? bcc = null, Collection<string>? cc = null, IDictionary<string, byte[]>? attachmentData = null, IDictionary<string, string>? headers = null, string? textBody = null)
{
    public Collection<string> To { get; } = to;

    public string Subject { get; } = subject;

    /// <summary>
    /// The HTML body. Every provider sends it as <c>text/html</c>, so a caller that passes bare text
    /// gets a message whose URLs are not links (most clients do not auto-link inside HTML) and whose
    /// interpolated values are parsed as markup. Build it with <see cref="HtmlEmail"/> and put the
    /// plain version in <see cref="TextBody"/>.
    /// </summary>
    public string? Body { get; } = body;

    /// <summary>
    /// Optional <c>text/plain</c> alternative. SMTP sends it beside <see cref="Body"/> as
    /// <c>multipart/alternative</c>; SendGrid passes it as the text part. Text-only clients read it,
    /// and spam filters score HTML-only mail worse. It is the last constructor parameter so every
    /// existing positional caller keeps compiling.
    /// </summary>
    public string? TextBody { get; } = textBody;

    public string? From { get; } = from;

    public string? DisplayName { get; } = displayName;

    public string? ReplyTo { get; } = replyTo;

    public string? ReplyToName { get; } = replyToName;

    public Collection<string> Bcc { get; } = bcc ?? new Collection<string>();

    public Collection<string> Cc { get; } = cc ?? new Collection<string>();

    public IDictionary<string, byte[]> AttachmentData { get; } = attachmentData ?? new Dictionary<string, byte[]>();

    public IDictionary<string, string> Headers { get; } = headers ?? new Dictionary<string, string>();
}