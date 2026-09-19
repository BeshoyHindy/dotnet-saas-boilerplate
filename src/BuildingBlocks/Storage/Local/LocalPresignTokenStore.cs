using Boilerplate.BuildingBlocks.Storage.Keys;
using System.Collections.Concurrent;

namespace Boilerplate.BuildingBlocks.Storage.Local;

/// <summary>
/// In-memory store of short-lived upload tokens for the local-storage development fallback.
/// Production deployments use S3 — this exists so dev/test setups without MinIO still work.
/// Singleton lifetime; tokens are one-shot.
/// </summary>
/// <remarks>
/// A token is <b>bound to the tenant that owns its key</b>. The store holds no tenant of its own —
/// the physical key already names one — so redeeming a token asks for the tenant doing the
/// redeeming and refuses when the key is not theirs. Without that, a token string minted under
/// tenant A would be a bearer capability for whatever key it happened to carry, and the tenant
/// prefix in the key would buy nothing here.
/// </remarks>
public sealed class LocalPresignTokenStore
{
    private readonly ConcurrentDictionary<string, LocalPresignToken> _tokens = new(StringComparer.Ordinal);

    /// <summary>
    /// Mints a one-shot token for <c>storageKey</c>, which must be a physical, already-authorized
    /// key — <c>LocalStorageService.GenerateUploadUrlAsync</c> passes what the ownership check
    /// returned, never raw caller input.
    /// </summary>
    public string Issue(string storageKey, string contentType, long maxBytes, TimeSpan ttl)
    {
        var token = Guid.NewGuid().ToString("N");
        _tokens[token] = new LocalPresignToken(storageKey, contentType, maxBytes, DateTimeOffset.UtcNow.Add(ttl));
        return token;
    }

    /// <summary>
    /// Redeems <paramref name="token"/> for <paramref name="tenantId"/>. Returns <c>null</c> when
    /// the token is unknown, already used, expired, or carries a key that tenant does not own — the
    /// same answer in every case, so a caller learns nothing from a refusal. The token is consumed
    /// either way: a wrong-tenant attempt burns it rather than leaving it to be retried.
    /// </summary>
    public LocalPresignToken? Consume(string token, string tenantId)
    {
        if (!_tokens.TryRemove(token, out var entry))
        {
            return null;
        }

        if (entry.ExpiresAt < DateTimeOffset.UtcNow)
        {
            return null;
        }

        return TenantStorageKeyRules.TryAuthorize(tenantId, entry.StorageKey, out _) ? entry : null;
    }
}

public sealed record LocalPresignToken(string StorageKey, string ContentType, long MaxBytes, DateTimeOffset ExpiresAt);
