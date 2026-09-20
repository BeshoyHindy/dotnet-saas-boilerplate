namespace Boilerplate.BuildingBlocks.Web.Idempotency;

/// <summary>
/// A cached HTTP response for idempotent replay — the filter's own entry format, written to and read
/// from <c>IDistributedCache</c> as bare JSON by <see cref="IdempotencyEndpointFilter"/> and nothing
/// else.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="V"/> is a wire version, and it is load-bearing.</b> Entries outlive a deploy (24h TTL
/// by default), so a running instance will read entries written by the previous build. A reader that
/// finds a version it does not know treats the entry as a miss and re-runs the handler; it never
/// tries to interpret it. That is the whole reason the field exists: without it, a format change —
/// or any foreign bytes under the key, such as the framed payload HybridCache writes — becomes a
/// deserialization exception on the replay path, which is how issue #82 turned the first replay of
/// every key into a 500.
/// </para>
/// <para>
/// <see cref="Headers"/> holds only the response headers the filter is willing to replay
/// (<see cref="IdempotencyEndpointFilter"/> owns that allow-list). Nothing else is stored at all —
/// a <c>Set-Cookie</c> never reaches the cache, so it cannot be handed to a second caller even by a
/// future reader that forgot to filter.
/// </para>
/// </remarks>
public sealed record CachedIdempotentResponse
{
    /// <summary>The entry format this build writes. Bump it whenever the shape below changes.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Wire-format version. Anything else is a miss, never an attempted read.</summary>
    public int V { get; init; } = CurrentVersion;

    /// <summary>The status code of the captured response.</summary>
    public int StatusCode { get; init; }

    /// <summary>The captured <c>Content-Type</c>, replayed verbatim.</summary>
    public string? ContentType { get; init; }

    /// <summary>
    /// A hash of the request that produced this response (query string + the bound payload arguments),
    /// or <see langword="null"/> when the request could not be fingerprinted. A second request under
    /// the same key with a different fingerprint is a client bug, not a retry, and is refused.
    /// </summary>
    public string? RequestFingerprint { get; init; }

    /// <summary>The allow-listed response headers to replay, if any.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>The captured response body, replayed byte for byte.</summary>
    public byte[] Body { get; init; } = [];
}
