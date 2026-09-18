using System.Globalization;

namespace Boilerplate.Modules.Notifications.IntegrationEventHandlers;

/// <summary>
/// Builds the subject + HTML body for tenant-lifecycle emails. Plain interpolated HTML (the framework
/// has no template engine); kept here so the handlers stay thin and the copy is easy to review.
/// </summary>
internal static class TenantLifecycleEmailBodies
{
    private static string Date(DateTime utc) => utc.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);

    public static (string Subject, string Body) NearingExpiry(string tenantName, DateTime validUpto, int daysRemaining)
    {
        var subject = daysRemaining <= 1
            ? "Your account expires tomorrow"
            : $"Your account expires in {daysRemaining} days";
        var body = Wrap(subject,
            $"<p>Hi {Escape(tenantName)},</p>" +
            $"<p>Your account is valid until <strong>{Date(validUpto)}</strong> " +
            $"({daysRemaining} day(s) remaining).</p>" +
            "<p>Please contact your account operator to renew and avoid any interruption to your service.</p>");
        return (subject, body);
    }

    public static (string Subject, string Body) EnteredGrace(string tenantName, DateTime validUpto, DateTime graceEnds)
    {
        const string subject = "Your account has lapsed — grace period active";
        var body = Wrap(subject,
            $"<p>Hi {Escape(tenantName)},</p>" +
            $"<p>Your account expired on <strong>{Date(validUpto)}</strong>. Your service continues " +
            $"during a grace period that ends on <strong>{Date(graceEnds)}</strong>.</p>" +
            "<p>Please renew before the grace period ends to keep your access uninterrupted.</p>");
        return (subject, body);
    }

    public static (string Subject, string Body) Expired(string tenantName, DateTime validUpto)
    {
        const string subject = "Your account has expired";
        var body = Wrap(subject,
            $"<p>Hi {Escape(tenantName)},</p>" +
            $"<p>Your account expired on <strong>{Date(validUpto)}</strong> and the grace period has " +
            "ended, so access is now suspended.</p>" +
            "<p>Contact your account operator to renew and restore access.</p>");
        return (subject, body);
    }

    private static string Wrap(string heading, string innerHtml) =>
        "<div style=\"font-family:Arial,Helvetica,sans-serif;font-size:14px;color:#1a1a1a;line-height:1.5\">" +
        $"<h2 style=\"font-size:18px;margin:0 0 12px\">{Escape(heading)}</h2>" +
        innerHtml +
        "<p style=\"margin-top:24px;color:#6b7280;font-size:12px\">This is an automated message.</p>" +
        "</div>";

    private static string Escape(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
             .Replace("<", "&lt;", StringComparison.Ordinal)
             .Replace(">", "&gt;", StringComparison.Ordinal);
}
