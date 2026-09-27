using Boilerplate.Modules.Identity.Data;

namespace Identity.Tests.Data;

public sealed class ContainsPatternTests
{
    [Fact]
    public void For_Should_WrapTermInWildcards()
    {
        ContainsPattern.For("alice").ShouldBe("%alice%");
    }

    [Theory]
    [InlineData("50%", "%50\\%%")]
    [InlineData("a_b", "%a\\_b%")]
    [InlineData("c:\\x", "%c:\\\\x%")]
    public void For_Should_EscapeLikeMetacharacters_When_TermContainsThem(string term, string expected)
    {
        // The term matches literally, as string.Contains did before search moved to ILIKE.
        ContainsPattern.For(term).ShouldBe(expected);
    }
}
