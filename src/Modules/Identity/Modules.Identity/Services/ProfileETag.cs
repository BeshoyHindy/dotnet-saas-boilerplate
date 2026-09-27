using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;

namespace Boilerplate.Modules.Identity.Services;

/// <summary>
/// The version of a user's profile on the wire for optimistic concurrency: <c>AppUser.ConcurrencyStamp</c>
/// as a strong entity tag. <c>GET /identity/profile</c> sends it as <c>ETag</c>; <c>PUT /identity/profile</c>
/// honours it in an optional <c>If-Match</c>.
///
/// <para>The PUT is a full-representation update, so without this two editors silently overwrite
/// each other. The stamp is rotated by every <c>UserManager</c> save, which is what makes it a
/// version.</para>
/// </summary>
internal static class ProfileETag
{
    /// <summary>The strong entity tag for a stamp, or null when the row has none.</summary>
    public static string? Format(string? concurrencyStamp) =>
        string.IsNullOrEmpty(concurrencyStamp) ? null : $"\"{concurrencyStamp}\"";

    /// <summary>
    /// RFC 9110 §13.1.1: true when <paramref name="ifMatch"/> is absent (the header is optional),
    /// is <c>*</c>, or lists the current tag under the strong comparison. A weak tag never matches,
    /// and neither does a value that does not parse — a precondition that cannot be read is not met.
    /// </summary>
    public static bool Matches(string? ifMatch, string? concurrencyStamp)
    {
        if (ifMatch is null)
        {
            return true;
        }

        if (!EntityTagHeaderValue.TryParseList(new StringValues(ifMatch), out var tags) || tags.Count == 0)
        {
            return false;
        }

        if (tags.Any(tag => tag.Equals(EntityTagHeaderValue.Any)))
        {
            return true;
        }

        var current = Format(concurrencyStamp);
        return current is not null
            && tags.Any(tag => tag.Compare(new EntityTagHeaderValue(current), useStrongComparison: true));
    }
}
