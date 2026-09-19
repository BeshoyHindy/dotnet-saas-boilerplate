using Mediator;

namespace Boilerplate.Modules.Identity.Contracts.v1.Impersonation.EndImpersonation;

public sealed record EndImpersonationCommand() : ICommand<EndImpersonationResponse>;

/// <summary>
/// The actor's token after stepping out of impersonation. Access-only by design: a refresh token is
/// a session row in the actor's own tenant, which this call — running inside the impersonated
/// tenant's context — must not write to. See <c>EndImpersonationCommandHandler</c>.
/// </summary>
public sealed record EndImpersonationResponse(
    string AccessToken,
    DateTime AccessTokenExpiresAt);
