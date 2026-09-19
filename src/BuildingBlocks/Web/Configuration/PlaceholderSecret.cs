namespace Boilerplate.BuildingBlocks.Web.Configuration;

/// <summary>
/// Recognises the secret values a template ships with: sample keys, dev defaults and the strings
/// people type when they mean "fill this in later". Shipping one to Production is indistinguishable
/// from having no secret at all, because the value is public in the repository.
/// </summary>
public static class PlaceholderSecret
{
    private static readonly string[] Markers =
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
        "todo",
        "sample",
        "example",
        "your-",
        "xxx",
        "secret",
        "password",
        "minioadmin",
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

        return Array.Exists(Markers, marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }
}
