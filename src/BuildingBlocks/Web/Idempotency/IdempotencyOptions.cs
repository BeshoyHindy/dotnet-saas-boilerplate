namespace Boilerplate.BuildingBlocks.Web.Idempotency;

/// <summary>
/// Configuration options for HTTP request idempotency.
/// </summary>
public sealed class IdempotencyOptions
{
    /// <summary>
    /// The header name to read the idempotency key from. Default: "Idempotency-Key".
    /// </summary>
    public string HeaderName { get; set; } = "Idempotency-Key";

    /// <summary>
    /// Default time-to-live for cached idempotent responses. Default: 24 hours.
    /// </summary>
    public TimeSpan DefaultTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    /// Maximum allowed length for the idempotency key. Default: 128 characters.
    /// </summary>
    public int MaxKeyLength { get; set; } = 128;

    /// <summary>
    /// How long a duplicate waits for the in-flight request holding the same key before giving up and
    /// running the handler itself. Default: 5 seconds. Must be greater than zero.
    /// </summary>
    /// <remarks>
    /// The in-process lock is held across the whole handler, so this is a bound on how long one slow
    /// handler may park its duplicates — not on how long the handler may take. Timing out is a
    /// deliberate loosening, not a failure: it widens the same "the handler may run twice" window that
    /// is already open across instances, and it is why a retry storm against a slow endpoint cannot
    /// pin a request thread per duplicate for the handler's full duration.
    /// </remarks>
    public TimeSpan LockWaitTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
