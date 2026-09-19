using System.Text.RegularExpressions;

namespace Boilerplate.BuildingBlocks.Storage.Keys;

/// <summary>
/// The one place in the product that writes the Storage block's key roots, and the only algorithm
/// that decides whether a key belongs to a tenant (ADR-0002).
///
/// <para><b>Two spaces, both tenant-prefixed.</b> <c>tenants/{tenantId}/…</c> is private;
/// <c>uploads/tenants/{tenantId}/…</c> is public. Callers never write either root or a tenant id:
/// they hand over a tenant-<i>relative</i> path plus a <see cref="StorageSpace"/> and get back an
/// opaque handle they may persist.</para>
///
/// <para><b>Ownership is decided on whole segments.</b> The prefix compared always ends in
/// <c>'/'</c>, so tenant <c>acme</c> never matches <c>acme-2</c>, and the comparison is
/// <see cref="StringComparison.Ordinal"/>, so no case variant of a root or a tenant id gets in.
/// Everything after the prefix must be a well-formed relative path, which is what rules out
/// <c>..</c>, <c>.</c>, <c>//</c>, leading and trailing slashes, backslashes and percent-encoded
/// separators in one stroke: the grammar admits no character that could express them.</para>
///
/// <para>Static, with the ambient tenant passed in, so the algorithm is testable on its own and so
/// <see cref="Local.LocalPresignTokenStore"/> — which has no DI — can bind a token to its key's
/// owner. <see cref="ITenantStorageKeys"/> is the DI-facing wrapper that supplies the tenant.</para>
/// </summary>
public static partial class TenantStorageKeyRules
{
    /// <summary>Root of the private key space, trailing slash included.</summary>
    public const string PrivateRoot = "tenants/";

    /// <summary>
    /// Root of the public key space, trailing slash included. Nested under <c>uploads/</c> on
    /// purpose — that is the prefix the deploy bucket policy publishes.
    /// </summary>
    public const string PublicRoot = "uploads/tenants/";

    /// <summary>
    /// A tenant id is a lowercase slug — the same pattern
    /// <c>CreateTenantCommandValidator.IdPattern</c> enforces at creation time. Restated here rather
    /// than shared because a BuildingBlock may not reference a module; the two must stay identical,
    /// and an id that fails this check can never own a key.
    /// </summary>
    public const string TenantIdPattern = "^[a-z0-9][a-z0-9-]{1,62}$";

    /// <summary>S3's object-key ceiling, in UTF-16 chars. Local storage is stricter per segment.</summary>
    public const int MaxKeyLength = 1024;

    /// <summary>Most filesystems cap a single path component at 255 bytes; S3 has no per-segment cap.</summary>
    private const int MaxSegmentLength = 255;

    [GeneratedRegex(TenantIdPattern, RegexOptions.CultureInvariant)]
    private static partial Regex TenantIdSlug();

    /// <summary>True when <paramref name="tenantId"/> may own a key at all.</summary>
    public static bool IsValidTenantId(string? tenantId) =>
        !string.IsNullOrEmpty(tenantId) && TenantIdSlug().IsMatch(tenantId);

    /// <summary>The root of <paramref name="space"/>, trailing slash included.</summary>
    public static string RootOf(StorageSpace space) =>
        space == StorageSpace.Public ? PublicRoot : PrivateRoot;

    /// <summary>
    /// The first path segment of each key root — the one literal an S3 bucket name or the first
    /// segment of <c>Storage:S3:Prefix</c> must never collide with. <c>StripSegment</c> in
    /// <c>S3StorageService.ToLogicalKey</c> eats whatever segment matches the bucket/prefix off the
    /// front of a URL path; a bucket literally named <c>uploads</c> or <c>tenants</c> would make it
    /// eat the key's own first segment instead. Derived from <see cref="PrivateRoot"/> and
    /// <see cref="PublicRoot"/> rather than restated, so this stays the one place those roots are
    /// spelled (the architecture scan enforces that for string literals; this keeps it true here too).
    /// </summary>
    public static IReadOnlyList<string> KeyRootSegments { get; } =
        [FirstSegmentOf(PrivateRoot), FirstSegmentOf(PublicRoot)];

    private static string FirstSegmentOf(string root) => root[..root.IndexOf('/', StringComparison.Ordinal)];

    /// <summary>
    /// The full prefix a tenant owns in <paramref name="space"/>, trailing slash included. The
    /// trailing slash is what makes the comparison a whole-segment one.
    /// </summary>
    public static string PrefixFor(string tenantId, StorageSpace space)
    {
        RequireTenantId(tenantId);
        return string.Concat(RootOf(space), tenantId, "/");
    }

    /// <summary>
    /// Composes the physical key for <paramref name="relativePath"/> in <paramref name="space"/>.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// The tenant id is not a slug, or the relative path is not well-formed — absolute, empty,
    /// containing <c>..</c>, <c>//</c>, a backslash, a percent sign or any character outside
    /// <c>[A-Za-z0-9._-]</c>.
    /// </exception>
    public static string Compose(string tenantId, StorageSpace space, string relativePath)
    {
        RequireTenantId(tenantId);

        if (!IsWellFormedRelativePath(relativePath))
        {
            throw new ArgumentException(
                "A storage path must be a non-empty, tenant-relative path of '/'-separated segments " +
                "drawn from [A-Za-z0-9._-]; it may not be absolute, contain '.' or '..' segments, " +
                "empty segments, or percent-encoded separators.",
                nameof(relativePath));
        }

        var key = string.Concat(PrefixFor(tenantId, space), relativePath);
        return key.Length <= MaxKeyLength
            ? key
            : throw new ArgumentException(
                $"The composed storage key is {key.Length} characters, over the {MaxKeyLength} limit.",
                nameof(relativePath));
    }

    /// <summary>
    /// Proves <paramref name="candidate"/> is a key this tenant owns. On success
    /// <paramref name="key"/> is the key itself (this method never repairs a key — a handle that is
    /// not already exactly right is refused, not normalised into something that is).
    /// </summary>
    public static bool TryAuthorize(string tenantId, string? candidate, out string key)
    {
        key = string.Empty;

        if (!IsValidTenantId(tenantId)
            || string.IsNullOrWhiteSpace(candidate)
            || candidate.Length > MaxKeyLength)
        {
            return false;
        }

        foreach (var space in (ReadOnlySpan<StorageSpace>)[StorageSpace.Private, StorageSpace.Public])
        {
            var prefix = string.Concat(RootOf(space), tenantId, "/");
            if (!candidate.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!IsWellFormedRelativePath(candidate[prefix.Length..]))
            {
                return false;
            }

            key = candidate;
            return true;
        }

        return false;
    }

    /// <summary>
    /// A tenant-relative path: one or more <c>'/'</c>-separated segments, each 1..255 characters of
    /// <c>[A-Za-z0-9._-]</c> and neither <c>.</c> nor <c>..</c>. An empty segment is what rejects a
    /// leading slash, a trailing slash and a doubled slash; the character set is what rejects
    /// backslashes, spaces and anything percent-encoded.
    /// </summary>
    public static bool IsWellFormedRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxKeyLength)
        {
            return false;
        }

        var start = 0;
        for (var i = 0; i <= path.Length; i++)
        {
            if (i != path.Length && path[i] != '/')
            {
                continue;
            }

            if (!IsWellFormedSegment(path.AsSpan(start, i - start)))
            {
                return false;
            }

            start = i + 1;
        }

        return true;
    }

    private static bool IsWellFormedSegment(ReadOnlySpan<char> segment)
    {
        if (segment.IsEmpty || segment.Length > MaxSegmentLength)
        {
            return false;
        }

        if (segment is "." or "..")
        {
            return false;
        }

        foreach (var c in segment)
        {
            var allowed = (c is >= 'a' and <= 'z')
                || (c is >= 'A' and <= 'Z')
                || (c is >= '0' and <= '9')
                || c is '.' or '_' or '-';

            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }

    private static void RequireTenantId(string tenantId)
    {
        if (!IsValidTenantId(tenantId))
        {
            throw new ArgumentException(
                $"'{tenantId}' is not a valid tenant id: storage keys require {TenantIdPattern}.",
                nameof(tenantId));
        }
    }
}
