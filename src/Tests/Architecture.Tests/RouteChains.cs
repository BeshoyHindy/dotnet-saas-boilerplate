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
/// <para>
/// <b>Interpolated string literals are understood, not just skipped.</b> A naive "find the matching
/// quote" scan treats the first quote inside an interpolation hole (<c>$"{Url("https://x")}"</c>) as
/// the literal's own closing quote, resumes scanning in what it now thinks is code, and a <c>//</c>
/// later in the same string — a URL, say — is misread as a comment that deletes the rest of the line,
/// taking a real call with it. <see cref="EndOfLiteral"/> instead finds the true end of a
/// <c>$"…"</c>/<c>$@"…"</c>/<c>@$"…"</c> literal by walking its interpolation holes with balanced-brace
/// counting (honouring <c>{{</c>/<c>}}</c> escapes) and recursing into whatever literal — plain,
/// verbatim, or itself interpolated — appears inside a hole. A <c>"""…"""</c> raw string literal,
/// interpolated or not, needs none of this: its end is the next run of quotes as long as the opening
/// fence, full stop, regardless of what its content looks like.
/// </para>
/// <para>
/// <b>An unparseable construct fails loudly.</b> If a literal or a hole never finds its close, the
/// scan throws a <see cref="RouteChainParseException"/> naming the source and the position, rather
/// than falling back to guessing and silently producing a chain (or a stripped chain) that is missing
/// text a real caller wrote. A scanner that goes quiet on the input it cannot handle is worse than one
/// that has no opinion at all.
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
    /// <param name="source">The file (or inline test) text to scan.</param>
    /// <param name="sourceName">
    /// Named for a <see cref="RouteChainParseException"/> message only — pass the file path when
    /// scanning a real file, so a parse failure points somewhere.
    /// </param>
    public static IReadOnlyList<string> Split(string source, string sourceName = "<inline source>")
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

            var end = EndOfStatement(source, start.Index + start.Length, sourceName);
            chains.Add(StripComments(source[start.Index..end], sourceName));
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
    private static string StripComments(string chain, string sourceName)
    {
        var result = new System.Text.StringBuilder(chain.Length);
        var i = 0;

        while (i < chain.Length)
        {
            var c = chain[i];

            if (c is '"' or '\'')
            {
                var closingQuote = EndOfLiteral(chain, i, sourceName);
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
    private static int EndOfStatement(string source, int from, string sourceName)
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
                i = EndOfLiteral(source, i, sourceName) + 1;
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
    /// literals (<c>"""…"""</c>) are matched quote-run to quote-run — interpolated or not, their
    /// content cannot move that boundary, so they need no further understanding of what is inside
    /// them. A non-raw literal honours backslash escapes (a verbatim one has none), and a non-raw
    /// interpolated literal (<c>$"…"</c>, <c>$@"…"</c>/<c>@$"…"</c>) is handed to
    /// <see cref="EndOfInterpolatedLiteral"/>, which is the one that has to understand its holes.
    /// </summary>
    private static int EndOfLiteral(string source, int start, string sourceName)
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

        var (verbatim, interpolated) = LiteralPrefix(source, start);

        if (quote == '"' && interpolated)
        {
            return EndOfInterpolatedLiteral(source, start, verbatim, sourceName);
        }

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

    /// <summary>
    /// Whether the quote starting at <paramref name="quoteStart"/> is preceded by <c>@</c> (verbatim)
    /// and/or <c>$</c> (interpolated), in either order (<c>$@"…"</c> and <c>@$"…"</c> both compile).
    /// </summary>
    private static (bool Verbatim, bool Interpolated) LiteralPrefix(string source, int quoteStart)
    {
        var verbatim = false;
        var interpolated = false;
        var i = quoteStart - 1;

        while (i >= 0 && (source[i] == '@' || source[i] == '$'))
        {
            if (source[i] == '@')
            {
                verbatim = true;
            }
            else
            {
                interpolated = true;
            }

            i--;
        }

        return (verbatim, interpolated);
    }

    /// <summary>
    /// The index of the closing quote of a non-raw interpolated literal (<c>$"…"</c> or its verbatim
    /// form) starting at <paramref name="start"/>. Walks the same escape rule as a plain literal for
    /// everything outside a hole, plus: <c>{{</c>/<c>}}</c> are escaped braces, not a hole; a bare
    /// <c>{</c> opens one, handed to <see cref="EndOfInterpolationHole"/> to find where it closes.
    /// </summary>
    private static int EndOfInterpolatedLiteral(string source, int start, bool verbatim, string sourceName)
    {
        var quote = source[start];
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
                if (verbatim && i + 1 < source.Length && source[i + 1] == quote)
                {
                    i += 2;
                    continue;
                }

                return i;
            }

            if (source[i] == '{')
            {
                if (i + 1 < source.Length && source[i + 1] == '{')
                {
                    i += 2; // Escaped brace: literal text, not a hole.
                    continue;
                }

                i = EndOfInterpolationHole(source, i, sourceName) + 1;
                continue;
            }

            if (source[i] == '}' && i + 1 < source.Length && source[i + 1] == '}')
            {
                i += 2; // Escaped closing brace outside any hole.
                continue;
            }

            i++;
        }

        throw new RouteChainParseException(sourceName, start, "an interpolated string literal that never closes");
    }

    /// <summary>
    /// The index of the <c>}</c> that closes the interpolation hole opened at
    /// <paramref name="openBraceIndex"/>, found by counting brace depth and skipping — not entering —
    /// every literal along the way (a string inside a hole, interpolated or not, is resolved by
    /// recursing into <see cref="EndOfLiteral"/>, which is what lets <c>$"{Url("https://x")}"</c> and a
    /// nested interpolated string inside a hole both resolve correctly instead of ending the outer
    /// literal early).
    /// </summary>
    private static int EndOfInterpolationHole(string source, int openBraceIndex, string sourceName)
    {
        var depth = 1;
        var i = openBraceIndex + 1;

        while (i < source.Length)
        {
            var c = source[i];

            if (c is '"' or '\'')
            {
                i = EndOfLiteral(source, i, sourceName) + 1;
                continue;
            }

            if (c == '{')
            {
                depth++;
                i++;
                continue;
            }

            if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return i;
                }

                i++;
                continue;
            }

            i++;
        }

        throw new RouteChainParseException(sourceName, openBraceIndex, "an interpolation hole that never closes");
    }
}

/// <summary>
/// Thrown by <see cref="RouteChains"/> when it meets a construct it cannot confidently parse. The
/// alternative — falling back to a naive scan — is how the interpolation-unaware version of this
/// scanner silently truncated a chain mid-string and dropped the rest of a route's text as if it were
/// a comment; failing loudly here is deliberately the louder, more annoying option.
/// </summary>
public sealed class RouteChainParseException : Exception
{
    public RouteChainParseException()
    {
    }

    public RouteChainParseException(string message)
        : base(message)
    {
    }

    public RouteChainParseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal RouteChainParseException(string sourceName, int index, string reason)
        : base($"{sourceName}: could not parse {reason} (near index {index}).")
    {
    }
}
