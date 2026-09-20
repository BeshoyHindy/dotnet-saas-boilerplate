using System.Text.RegularExpressions;

namespace Architecture.Tests;

/// <summary>
/// Splits endpoint source into one string per route registration — everything from a
/// <c>MapPost(</c> / <c>MapGet(</c> / … to the <c>;</c> that ends its builder chain.
/// </summary>
/// <remarks>
/// <para>
/// Whole-file text is the wrong unit for anything that asks "what else is on this route": a file
/// that maps two routes would have the second route's calls read as if they were the first's. The
/// scan below is depth- and literal-aware rather than "up to the next semicolon", because a chain's
/// own lambda body is full of semicolons — <c>var result = await mediator.Send(…);</c> sits inside
/// the <c>Map…(</c> parentheses of every endpoint in this repository.
/// </para>
/// <para>
/// It is still text, and it says so: a chain assembled across statements through a local variable is
/// outside its reach. What it buys is that the tests built on it name a route rather than a file.
/// </para>
/// <para>
/// <b>Comments are stripped from the chain it returns.</b> A callers' own text — including a comment
/// explaining why a call is <i>absent</i> — sits inside the same substring a regex-based test greps,
/// so a comment that names a method (<c>// not .WithIdempotency()</c>) would read as a call the source
/// never makes. <see cref="Split"/> removes <c>//</c> and <c>/* */</c> comments before handing a chain
/// back, the same literal-aware way <see cref="EndOfStatement"/> already has to walk past them to find
/// the closing <c>;</c>; a comment-like sequence inside a string literal is left alone.
/// </para>
/// </remarks>
internal static partial class RouteChains
{
    [GeneratedRegex(@"\bMap[A-Z]\w*\(", RegexOptions.CultureInvariant)]
    private static partial Regex MapCall();

    /// <summary>
    /// Every route registration in <paramref name="source"/>, each as the text of its own chain, with
    /// comments removed. Nested <c>Map…(</c> calls inside an already-open chain are skipped: they
    /// belong to it.
    /// </summary>
    public static IReadOnlyList<string> Split(string source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var chains = new List<string>();
        var consumedTo = 0;

        foreach (Match start in MapCall().Matches(source))
        {
            if (start.Index < consumedTo)
            {
                continue;
            }

            var end = EndOfStatement(source, start.Index + start.Length);
            chains.Add(StripComments(source[start.Index..end]));
            consumedTo = end;
        }

        return chains;
    }

    /// <summary>
    /// <paramref name="chain"/> with every <c>//</c> and <c>/* */</c> comment removed, outside any
    /// string literal. Mirrors the literal handling in <see cref="EndOfStatement"/> rather than
    /// sharing code with it, because that scan only needs to find an end index and this one needs to
    /// rebuild the text around what it skips.
    /// </summary>
    private static string StripComments(string chain)
    {
        var result = new System.Text.StringBuilder(chain.Length);
        var i = 0;

        while (i < chain.Length)
        {
            var c = chain[i];

            if (c is '"' or '\'')
            {
                var closingQuote = EndOfLiteral(chain, i);
                result.Append(chain, i, closingQuote - i + 1);
                i = closingQuote + 1;
                continue;
            }

            if (c == '/' && i + 1 < chain.Length && chain[i + 1] == '/')
            {
                var newline = chain.IndexOf('\n', i);
                if (newline < 0)
                {
                    break;
                }

                result.Append('\n');
                i = newline + 1;
                continue;
            }

            if (c == '/' && i + 1 < chain.Length && chain[i + 1] == '*')
            {
                var close = chain.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? chain.Length : close + 2;
                continue;
            }

            result.Append(c);
            i++;
        }

        return result.ToString();
    }

    /// <summary>
    /// The index just past the <c>;</c> that closes the statement opened at <paramref name="from"/>,
    /// which is the first one reached at zero paren and brace depth outside any literal or comment.
    /// </summary>
    private static int EndOfStatement(string source, int from)
    {
        // The regex match consumed the chain's opening '(', so the scan starts one level in.
        var parens = 1;
        var braces = 0;

        var i = from;
        while (i < source.Length)
        {
            var c = source[i];

            if (c is '"' or '\'')
            {
                i = EndOfLiteral(source, i) + 1;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '/')
            {
                var newline = source.IndexOf('\n', i);
                if (newline < 0)
                {
                    return source.Length;
                }

                i = newline + 1;
                continue;
            }

            if (c == '/' && i + 1 < source.Length && source[i + 1] == '*')
            {
                var close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = close < 0 ? source.Length : close + 2;
                continue;
            }

            switch (c)
            {
                case '(':
                    parens++;
                    break;
                case ')':
                    parens--;
                    break;
                case '{':
                    braces++;
                    break;
                case '}':
                    braces--;
                    break;
                case ';' when parens <= 0 && braces <= 0:
                    return i + 1;
                default:
                    break;
            }

            i++;
        }

        return source.Length;
    }

    /// <summary>
    /// The index of the closing quote of the literal starting at <paramref name="start"/>. Raw string
    /// literals (<c>"""…"""</c>) are matched quote-run to quote-run; ordinary ones honour backslash
    /// escapes, which a verbatim string has none of.
    /// </summary>
    private static int EndOfLiteral(string source, int start)
    {
        var quote = source[start];

        var run = 0;
        while (start + run < source.Length && source[start + run] == quote)
        {
            run++;
        }

        if (run >= 3)
        {
            var fence = new string(quote, run);
            var close = source.IndexOf(fence, start + run, StringComparison.Ordinal);
            return close < 0 ? source.Length - 1 : close + run - 1;
        }

        var verbatim = start > 0 && source[start - 1] == '@';
        var i = start + 1;
        while (i < source.Length)
        {
            if (!verbatim && source[i] == '\\')
            {
                i += 2;
                continue;
            }

            if (source[i] == quote)
            {
                // A doubled quote inside a verbatim string is an escaped quote, not the end.
                if (verbatim && i + 1 < source.Length && source[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i;
            }

            i++;
        }

        return source.Length - 1;
    }
}
