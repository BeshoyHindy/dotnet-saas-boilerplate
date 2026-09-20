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
/// </remarks>
internal static partial class RouteChains
{
    [GeneratedRegex(@"\bMap[A-Z]\w*\(", RegexOptions.CultureInvariant)]
    private static partial Regex MapCall();

    /// <summary>
    /// Every route registration in <paramref name="source"/>, each as the text of its own chain.
    /// Nested <c>Map…(</c> calls inside an already-open chain are skipped: they belong to it.
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
            chains.Add(source[start.Index..end]);
            consumedTo = end;
        }

        return chains;
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
