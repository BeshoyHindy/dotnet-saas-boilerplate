using Boilerplate.Modules.Notifications.IntegrationEventHandlers;

namespace Integration.Tests.Tests.Notifications;

/// <summary>
/// Pure coverage for the tenant-lifecycle mail bodies: no host, no containers. They build HTML through
/// Mailing's one encoder, so a tenant name is shown as text, never parsed as markup — including inside
/// an attribute, which the three-character escape they used before did not cover.
/// </summary>
public sealed class TenantLifecycleEmailBodiesTests
{
    private const string HostileName = "<script>alert(1)</script> \"Acme\" & Co";
    private const string EncodedName = "&lt;script&gt;alert(1)&lt;/script&gt; &quot;Acme&quot; &amp; Co";

    private static readonly DateTime ValidUpto = new(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);

    public static TheoryData<string, string> Bodies()
    {
        var nearing = TenantLifecycleEmailBodies.NearingExpiry(HostileName, ValidUpto, 7);
        var grace = TenantLifecycleEmailBodies.EnteredGrace(HostileName, ValidUpto, ValidUpto.AddDays(14));
        var expired = TenantLifecycleEmailBodies.Expired(HostileName, ValidUpto);
        return new TheoryData<string, string>
        {
            { nameof(TenantLifecycleEmailBodies.NearingExpiry), nearing.Body },
            { nameof(TenantLifecycleEmailBodies.EnteredGrace), grace.Body },
            { nameof(TenantLifecycleEmailBodies.Expired), expired.Body },
        };
    }

    #region Edge Cases

    [Theory]
    [MemberData(nameof(Bodies))]
    public void Body_Should_EncodeTheTenantName_When_ItContainsMarkupAndQuotes(string template, string body)
    {
        body.ShouldNotContain("<script>", customMessage: template);
        body.ShouldNotContain("\"Acme\"", customMessage: template);
        body.ShouldContain($"<p>Hi {EncodedName},</p>", customMessage: template);
    }

    #endregion

    #region Happy Path

    [Fact]
    public void Body_Should_KeepItsLayout_When_TheNameIsPlain()
    {
        // Behaviour is unchanged by the move to the shared encoder: same wrapper, same heading, same copy.
        var (subject, body) = TenantLifecycleEmailBodies.Expired("Acme", ValidUpto);

        subject.ShouldBe("Your account has expired");
        body.ShouldStartWith("<div style=\"font-family:Arial,Helvetica,sans-serif;");
        body.ShouldContain("<h2 style=\"font-size:18px;margin:0 0 12px\">Your account has expired</h2>");
        body.ShouldContain("<p>Hi Acme,</p>");
        body.ShouldContain("<strong>March 1, 2026</strong>");
    }

    #endregion
}
