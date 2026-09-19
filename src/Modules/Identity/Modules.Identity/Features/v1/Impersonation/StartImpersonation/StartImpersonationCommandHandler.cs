using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.Modules.Identity.Contracts.v1.Impersonation;
using Boilerplate.Modules.Identity.Contracts.v1.Impersonation.StartImpersonation;
using Boilerplate.Modules.Identity.Services;
using Mediator;

namespace Boilerplate.Modules.Identity.Features.v1.Impersonation.StartImpersonation;

/// <summary>
/// Same-tenant impersonation: an admin acts as one of their own tenant's users. Crossing a tenant
/// boundary is NOT done here — it is the operator token exchange
/// (<c>POST /identity/operator/token-exchange</c>), which is root-only, takes a reason and checks
/// the target tenant. Both mint through <see cref="IImpersonationTokenIssuer"/>, so they share one
/// grant table, one revocation list (by jti) and one lifetime ceiling.
/// </summary>
public sealed class StartImpersonationCommandHandler(
    ICurrentUser currentUser,
    IImpersonationTokenIssuer tokenIssuer)
    : ICommandHandler<StartImpersonationCommand, ImpersonationResponse>
{
    public async ValueTask<ImpersonationResponse> Handle(
        StartImpersonationCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!currentUser.IsAuthenticated())
        {
            throw new UnauthorizedException();
        }

        var actorUserId = currentUser.GetUserId().ToString();
        var actorTenantId = currentUser.GetTenant()
            ?? throw new UnauthorizedException("missing tenant context");

        // One tenant, one token (ADR-0002). A root operator reaching into another tenant exchanges
        // a token instead: that path carries the root-only permission, a mandatory reason and the
        // target-tenant checks. Root is not special-cased here — there is exactly one door.
        if (!string.Equals(actorTenantId, request.TargetTenantId, StringComparison.Ordinal))
        {
            throw new ForbiddenException(
                "cross-tenant impersonation goes through POST /identity/operator/token-exchange");
        }

        // Prevent self-impersonation (pointless, confuses the audit trail). Caller error → explicit 4xx,
        // not the 500 CustomException defaults to.
        if (string.Equals(actorUserId, request.TargetUserId, StringComparison.Ordinal))
        {
            throw new CustomException("cannot impersonate yourself", errors: null, System.Net.HttpStatusCode.BadRequest);
        }

        // Prevent nesting: a caller already acting as someone else (impersonation token or an
        // exchanged operator token) must step out first — act_* has to stay unambiguous.
        var callerClaims = currentUser.GetUserClaims();
        if (callerClaims is not null
            && callerClaims.Any(c => c.Type == ClaimConstants.ActorSubject))
        {
            throw new CustomException(
                "end current impersonation before starting a new one",
                errors: null,
                System.Net.HttpStatusCode.BadRequest);
        }

        var issued = await tokenIssuer.IssueAsync(
            new IssueActingTokenRequest(
                ActorUserId: actorUserId,
                ActorUserName: currentUser.Name,
                ActorTenantId: actorTenantId,
                TargetUserId: request.TargetUserId,
                TargetTenantId: request.TargetTenantId,
                Reason: request.Reason ?? string.Empty,
                RequestedMinutes: request.DurationMinutes),
            cancellationToken).ConfigureAwait(false);

        return new ImpersonationResponse(
            AccessToken: issued.AccessToken,
            AccessTokenExpiresAt: issued.ExpiresAtUtc,
            ActorUserId: actorUserId,
            ActorTenantId: actorTenantId,
            ImpersonatedUserId: issued.TargetUserId,
            ImpersonatedTenantId: request.TargetTenantId);
    }
}
