using Boilerplate.BuildingBlocks.Mailing;

namespace Framework.Tests.Mailing;

public sealed class HtmlEmailTests
{
    #region Encode

    [Fact]
    public void Encode_Should_NeutraliseMarkup_When_ValueContainsTags()
    {
        // Act
        var encoded = HtmlEmail.Encode("<script>alert(1)</script>");

        // Assert
        encoded.ShouldNotContain("<script>");
        encoded.ShouldBe("&lt;script&gt;alert(1)&lt;/script&gt;");
    }

    [Fact]
    public void Encode_Should_EncodeQuotes_When_ValueContainsThem()
    {
        // The hand-rolled escape Notifications used covered only & < >, which is safe in element
        // content but not inside an attribute. Quotes are the difference.

        // Act
        var encoded = HtmlEmail.Encode("a \" and an ' quote");

        // Assert
        encoded.ShouldNotContain("\"");
        encoded.ShouldNotContain("'");
        encoded.ShouldBe("a &quot; and an &#39; quote");
    }

    [Fact]
    public void Encode_Should_TreatInputAsText_When_ItLooksLikeAnEntity()
    {
        // The input is text, not markup: "&amp;" is five characters someone typed, and its ampersand is
        // escaped like any other. Encoding it again is the correct answer, not a double-encoding bug.

        // Act & Assert
        HtmlEmail.Encode("Tom &amp; Jerry").ShouldBe("Tom &amp;amp; Jerry");
        HtmlEmail.Encode("Tom & Jerry").ShouldBe("Tom &amp; Jerry");
    }

    [Fact]
    public void Encode_Should_EntitiseLatin1ButNotHigherPlanes_When_ValueIsNonAscii()
    {
        // WebUtility.HtmlEncode turns the Latin-1 supplement (160-255) into numeric entities and leaves
        // anything above verbatim, relying on the declared utf-8. Both render the same; pinned so the
        // asymmetry is known rather than discovered.

        // Act & Assert
        HtmlEmail.Encode("Conceição").ShouldBe("Concei&#231;&#227;o");
        HtmlEmail.Encode("日本語").ShouldBe("日本語");
    }

    [Fact]
    public void Encode_Should_ReturnEmpty_When_ValueIsEmpty()
    {
        HtmlEmail.Encode(string.Empty).ShouldBeEmpty();
    }

    [Fact]
    public void Encode_Should_Throw_When_ValueIsNull()
    {
        Should.Throw<ArgumentNullException>(() => HtmlEmail.Encode(null!));
    }

    #endregion

    #region Shell

    [Fact]
    public void Shell_Should_EmitCompleteDocument_When_Called()
    {
        // Act
        var html = HtmlEmail.Shell("Heading", "<p>body</p>");

        // Assert
        html.ShouldStartWith("<!DOCTYPE html>");
        html.ShouldContain("<meta charset=\"utf-8\">");
        html.ShouldContain("<title>Heading</title>");
        html.ShouldContain("<p>body</p>");
    }

    [Fact]
    public void Shell_Should_EncodeHeading_When_HeadingContainsMarkup()
    {
        // Act
        var html = HtmlEmail.Shell("<b>Hi</b>", "<p>body</p>");

        // Assert — the heading is plain text in both the title and the h1.
        html.ShouldNotContain("<b>Hi</b>");
        html.ShouldContain("<title>&lt;b&gt;Hi&lt;/b&gt;</title>");
    }

    [Fact]
    public void Shell_Should_InsertInnerHtmlVerbatim_When_ItContainsMarkup()
    {
        // Pins the documented contract: innerHtml is trusted markup the caller built and is NOT
        // encoded. "Hardening" it would render every mail as visible tags.

        // Act
        var html = HtmlEmail.Shell("Heading", "<p><strong>bold</strong></p>");

        // Assert
        html.ShouldContain("<p><strong>bold</strong></p>");
        html.ShouldNotContain("&lt;strong&gt;");
    }

    [Fact]
    public void Shell_Should_Throw_When_ArgumentIsNull()
    {
        Should.Throw<ArgumentNullException>(() => HtmlEmail.Shell(null!, "<p>x</p>"));
        Should.Throw<ArgumentNullException>(() => HtmlEmail.Shell("Heading", null!));
    }

    #endregion

    #region LinkAction

    [Fact]
    public void LinkAction_Should_RenderAnchor_When_Called()
    {
        // A bare URL inside a text/html part is not auto-linked by most clients; the action has to be
        // an anchor.

        // Act
        var html = HtmlEmail.LinkAction("Reset your password", "Use the link below.", "https://app.test/reset", "Reset password");

        // Assert
        html.ShouldContain("<a href=\"https://app.test/reset\"");
        html.ShouldContain(">Reset password</a>");
    }

    [Fact]
    public void LinkAction_Should_EncodeQuerySeparator_When_UrlHasSeveralParameters()
    {
        // An entity-encoded & inside href is what the HTML spec asks for; the browser decodes it. The
        // verbatim URL belongs in the text/plain part.

        // Act
        var html = HtmlEmail.LinkAction("Confirm", "Intro", "https://app.test/c?token=a&email=b%2Bc", "Confirm");

        // Assert
        html.ShouldContain("token=a&amp;email=b%2Bc");
        html.ShouldNotContain("token=a&email=b%2Bc");
    }

    [Fact]
    public void LinkAction_Should_KeepUrlInsideAttribute_When_UrlContainsAQuote()
    {
        // An unencoded quote would close href and turn the rest of the URL into attributes. Asserting the
        // encoded form alone is not enough: it would pass with the raw form emitted as well.

        // Act
        var html = HtmlEmail.LinkAction("Heading", "Intro", "https://app.test/x?q=\"onmouseover=alert(1)", "Go");

        // Assert
        html.ShouldNotContain("q=\"onmouseover");
        html.ShouldContain("&quot;onmouseover=alert(1)");
    }

    [Fact]
    public void LinkAction_Should_EncodeIntroAndLabel_When_TheyContainMarkup()
    {
        // Act
        var html = HtmlEmail.LinkAction("Heading", "<i>intro</i>", "https://app.test/x", "<i>label</i>");

        // Assert
        html.ShouldNotContain("<i>intro</i>");
        html.ShouldNotContain("<i>label</i>");
        html.ShouldContain("&lt;i&gt;intro&lt;/i&gt;");
        html.ShouldContain("&lt;i&gt;label&lt;/i&gt;");
    }

    #endregion

    #region Notice

    [Fact]
    public void Notice_Should_EncodeMessage_When_MessageContainsMarkup()
    {
        // Act
        var html = HtmlEmail.Notice("Welcome!", "Hi <script>alert(1)</script>, thanks for registering.");

        // Assert
        html.ShouldStartWith("<!DOCTYPE html>");
        html.ShouldNotContain("<script>");
        html.ShouldContain("&lt;script&gt;alert(1)&lt;/script&gt;");
    }

    #endregion
}
