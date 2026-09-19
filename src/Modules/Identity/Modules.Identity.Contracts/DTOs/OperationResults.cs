namespace Boilerplate.Modules.Identity.Contracts.DTOs;

/// <summary>
/// How many sessions a bulk revoke actually ended. Named rather than anonymous so it reaches
/// the exported OpenAPI document (ADR-0004) — the schema exporter has nothing to describe an
/// anonymous type with, and a client then has no type for the count it displays.
/// </summary>
public sealed record RevokeSessionsResponse(int RevokedCount);

/// <summary>
/// Outcome of a two-factor enrolment confirmation or a two-factor disable. Same reasoning as
/// <see cref="RevokeSessionsResponse"/>: the shape on the wire is unchanged, it is only named.
/// </summary>
public sealed record TwoFactorOperationResponse(bool Success);
