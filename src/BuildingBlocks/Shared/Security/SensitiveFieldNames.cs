namespace Boilerplate.BuildingBlocks.Shared.Security;

/// <summary>
/// The one list of property names that read as a secret. Anything matching it is dropped before a
/// request is hashed, logged or otherwise persisted in derived form.
/// </summary>
/// <remarks>
/// <para>
/// One place on purpose: a second copy is a place for the two to disagree, and the copy that forgets
/// <c>newPassword</c> is the one that writes a brute-forceable digest of a password into Redis.
/// Callers that need to be explicit at the property use
/// <see cref="NotFingerprintedAttribute"/> as well — the name list is the safety net, the attribute
/// is the statement of intent.
/// </para>
/// <para>
/// <b>Two kinds of match, because one size does not fit.</b> <see cref="Substrings"/> are words that
/// mean "secret" wherever they appear, so they match anywhere in the name (<c>confirmPassword</c>,
/// <c>currentPassword</c>, <c>refreshToken</c>). <see cref="Words"/> are short and ambiguous —
/// <c>code</c>, <c>key</c>, <c>otp</c> — so they match only as a whole name or as the last word of a
/// camelCase / snake_case name (<c>resetCode</c>, <c>api_key</c>), which keeps <c>encoded</c> and
/// <c>keyboard</c> out of it. It deliberately still catches innocents like <c>countryCode</c>: the
/// cost of a false positive is that a retry differing only in that field replays rather than being
/// refused, and the cost of a false negative is a secret in the cache.
/// </para>
/// </remarks>
public static class SensitiveFieldNames
{
    /// <summary>Words that mean "secret" wherever they appear in a name.</summary>
    private static readonly string[] Substrings =
    [
        "password",
        "passphrase",
        "secret",
        "token",
        "credential",

        // A connection string carries the credentials it connects with, so it is one. It is also the
        // field the first version of this list missed, on a command an idempotent endpoint binds
        // (CreateTenantCommand.ConnectionString). "connection" on its own would be too broad —
        // ConnectionId and ConnectionCount are not secrets.
        "connectionstring",
    ];

    /// <summary>
    /// Words too short or too common to match anywhere: matched as a whole name, or as the trailing
    /// word of a camelCase / snake_case / kebab-case name.
    /// </summary>
    private static readonly string[] Words =
    [
        "otp",
        "code",
        "key",
        "pin",
        "nonce",
        "signature",
    ];

    /// <summary>
    /// True when <paramref name="name"/> reads as a secret and must not be fingerprinted, logged or
    /// persisted in derived form.
    /// </summary>
    public static bool IsSensitive(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return Array.Exists(Substrings, s => name.Contains(s, StringComparison.OrdinalIgnoreCase))
            || Array.Exists(Words, w => EndsWithWord(name, w));
    }

    /// <summary>
    /// True when <paramref name="name"/> is <paramref name="word"/>, or ends with it at a word
    /// boundary — the start of the name, a separator, or a case change into the word.
    /// </summary>
    private static bool EndsWithWord(string name, string word)
    {
        if (!name.EndsWith(word, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var start = name.Length - word.Length;
        if (start == 0)
        {
            return true;
        }

        var previous = name[start - 1];
        return previous is '_' or '-' or '.' || char.IsUpper(name[start]);
    }
}
