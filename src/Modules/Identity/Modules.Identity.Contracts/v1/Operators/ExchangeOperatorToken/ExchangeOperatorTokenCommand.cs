using Mediator;

namespace Boilerplate.Modules.Identity.Contracts.v1.Operators.ExchangeOperatorToken;

/// <summary>
/// Root operator asks for a short-lived, access-only token that acts inside
/// <paramref name="TargetTenantId" /> (ADR-0002: "root operators cross tenants by exchanging
/// tokens, never by header"). The issued token's subject is a real user of the target tenant so
/// the normal permission pipeline applies unchanged; <c>act_sub</c>/<c>act_tenant</c> record who
/// is really acting.
/// </summary>
/// <param name="TargetTenantId">Tenant the operator wants to act in.</param>
/// <param name="TargetUserId">
/// Optional user inside the target tenant to act as. Omitted → the tenant's admin (resolved from
/// the tenant record's AdminEmail).
/// </param>
/// <param name="Reason">Required, audited justification.</param>
/// <param name="DurationMinutes">
/// Requested lifetime. Clamped server-side to <c>OperatorExchangeOptions.MaxMinutes</c>;
/// null → <c>OperatorExchangeOptions.DefaultMinutes</c>.
/// </param>
public sealed record ExchangeOperatorTokenCommand(
    string TargetTenantId,
    string? TargetUserId,
    string Reason,
    int? DurationMinutes = null)
    : ICommand<OperatorTokenExchangeResponse>;

/// <summary>
/// The exchanged token. Access-only on purpose: a refresh token is a row in the session store of
/// the tenant that mints it, and the operator's own session is never touched by the exchange, so
/// stepping back out is a client-side drop of this token — nothing to refresh, nothing to revoke.
/// </summary>
public sealed record OperatorTokenExchangeResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAt,
    string TargetTenantId,
    string TargetUserId,
    string? TargetUserName,
    string ActorUserId,
    string ActorTenantId,
    Guid GrantId,
    string Jti);
