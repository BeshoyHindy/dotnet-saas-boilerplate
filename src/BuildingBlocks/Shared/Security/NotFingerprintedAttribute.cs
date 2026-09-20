namespace Boilerplate.BuildingBlocks.Shared.Security;

/// <summary>
/// Marks a property that must never reach a request fingerprint — the hash the idempotency filter
/// stores alongside a cached response so it can tell a genuine retry from a key reused with a
/// different payload.
/// </summary>
/// <remarks>
/// <para>
/// A fingerprint is a digest of the request, and it lives in Redis for the entry's whole TTL. For a
/// low-entropy secret — a password, a one-time code — a digest is not a protection: an attacker who
/// reaches the cache can grind a candidate list against it offline. Keying the hash would not help
/// either, because the Data Protection keys are persisted in the <i>same</i> Redis
/// (<c>PersistKeysToStackExchangeRedis</c>), so a compromise that hands over one hands over the
/// other. The only answer is that the secret never enters the digest at all.
/// </para>
/// <para>
/// <see cref="SensitiveFieldNames"/> already excludes anything whose name reads as a secret, and it
/// is what protects a property nobody remembered to mark. This attribute is the explicit half: it
/// says so at the property, on the diff, for a field whose name does not give it away — and it keeps
/// working if the name list is ever narrowed. It applies to a handler <i>parameter</i> too, for the
/// endpoint that binds a bare value rather than a DTO; on a positional record use
/// <c>[property: NotFingerprinted]</c>, which is where the property actually lives.
/// </para>
/// <para>
/// <b>The consequence is deliberate.</b> A retry under the same Idempotency-Key that differs
/// <i>only</i> in an excluded field looks identical to the first request, so it replays the first
/// response instead of being refused with <c>422</c>. That is the right trade: a client that changes
/// only its password and reuses the key gets the earlier answer, which is the same answer idempotency
/// promises for any retry.
/// </para>
/// </remarks>
[AttributeUsage(
    AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter,
    AllowMultiple = false,
    Inherited = true)]
public sealed class NotFingerprintedAttribute : Attribute;
