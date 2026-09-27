namespace Boilerplate.Modules.Identity;

/// <summary>
/// The operator console's public origin, for the root tenant's own mailed links (config section
/// <c>"MailLinkOrigin"</c>). Optional: a <c>--frontend false</c> scaffold has no console to point
/// at, and every other tenant's mail keeps using <c>OriginOptions.OriginUrl</c> regardless. See
/// <see cref="Services.MailLinkOrigin"/> for how the two origins are chosen between.
/// </summary>
public sealed class MailLinkOriginOptions
{
    public const string SectionName = "MailLinkOrigin";

    public Uri? ConsoleOriginUrl { get; set; }
}
