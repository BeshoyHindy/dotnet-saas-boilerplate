using Mediator;

namespace Boilerplate.Modules.Identity.Contracts.v1.Impersonation.EndImpersonation;

public sealed record EndImpersonationCommand() : ICommand<EndImpersonationResponse>;

/// <summary>
/// Stepping out returns NO token. The actor's own session is never discarded while they act as
/// somebody else — the acting token is held separately (in memory, client-side) — so there is
/// nothing to restore server-side: the client drops the acting token and carries on with the
/// session it already has. That closes the old gap where End handed back an access-only token with
/// no refresh counterpart, a credential the actor could not renew. The grant is marked ended, so
/// the acting token is rejected on its very next request.
/// </summary>
public sealed record EndImpersonationResponse(
    string ActorUserId,
    string ActorTenantId,
    string ImpersonatedUserId,
    string ImpersonatedTenantId,
    DateTime EndedAtUtc);
