namespace Boilerplate.BuildingBlocks.Web.Configuration;

/// <summary>
/// Recognises the secret values a template ships with: sample keys, dev defaults and the strings
/// people type when they mean "fill this in later". Shipping one to Production is indistinguishable
/// from having no secret at all, because the value is public in the repository.
/// </summary>
public static class PlaceholderSecret
{
    /// <summary>
    /// Markers long and specific enough that finding them anywhere in a value is conclusive. A
    /// generated key will not contain "do-not-use-in-prod" by accident.
    /// </summary>
    private static readonly string[] SubstringMarkers =
    [
        "replace-with",
        "replace_me",
        "replaceme",
        "changeme",
        "change-me",
        "change_me",
        "placeholder",
        "dev-only",
        "development-only",
        "do-not-use-in-prod",
        "minioadmin",
        "your-",
    ];

    /// <summary>
    /// Short, ordinary words. A 48-byte base64 key contains random letter runs, so these are matched
    /// only as a whole word — the value itself, a delimited segment of it, or its leading segment —
    /// never as an arbitrary substring. Otherwise a perfectly good secret containing "…Sample…" or
    /// "…xxx…" by chance would block a deployment, and an operator who has to disable a security
    /// check learns to disable security checks.
    /// </summary>
    private static readonly string[] WordMarkers =
    [
        "todo",
        "sample",
        "example",
        "secret",
        "password",
        "xxx",
        "test",
    ];

    /// <summary>
    /// True when <paramref name="value"/> is empty or carries a placeholder marker. Intended for
    /// opaque secrets (signing keys, passwords, API keys) — never run it over a connection string,
    /// whose legitimate text contains words like "Password".
    /// </summary>
    public static bool Looks(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        if (Array.Exists(SubstringMarkers, marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return Segments(value).Any(segment => Array.Exists(WordMarkers, marker => IsWord(segment, marker)));
    }

    /// <summary>
    /// True when a delimited segment IS the marker, or the marker with a numeric suffix — "secret",
    /// "Password123", "test2" — the shapes people actually type. A marker buried in a longer run of
    /// characters ("k3xxxV9sample…") is left alone.
    /// </summary>
    private static bool IsWord(string segment, string marker)
    {
        if (segment.Length < marker.Length ||
            !segment.AsSpan(0, marker.Length).Equals(marker, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var i = marker.Length; i < segment.Length; i++)
        {
            if (!char.IsAsciiDigit(segment[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Splits a value on the separators people put between words (anything that is not a letter or a
    /// digit), so "my-secret-key" yields "secret" while a base64 run does not.
    /// </summary>
    private static IEnumerable<string> Segments(string value)
    {
        var start = 0;
        for (var i = 0; i <= value.Length; i++)
        {
            if (i == value.Length || !char.IsLetterOrDigit(value[i]))
            {
                if (i > start)
                {
                    yield return value[start..i];
                }

                start = i + 1;
            }
        }
    }
}
