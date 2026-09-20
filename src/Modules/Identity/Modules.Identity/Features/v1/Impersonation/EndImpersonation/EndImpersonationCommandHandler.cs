using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.Modules.Auditing.Contracts;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Impersonation.EndImpersonation;
using Mediator;
using Microsoft.Extensions.Logging;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;

namespace Boilerplate.Modules.Identity.Features.v1.Impersonation.EndImpersonation;

/// <summary>
/// Ends an acting session (same-tenant impersonation or an exchanged operator token) by marking
/// its grant ended — which is what actually kills the token, through the jti check in the JWT
/// validation hook. No token is minted: the actor's own session was never taken away, so the
/// client just stops sending the acting token and keeps the session it already has.
/// </summary>
public sealed class EndImpersonationCommandHandler(
    ISecurityAudit securityAudit,
    ICurrentUser currentUser,
    IRequestContext requestContext,
    IImpersonationGrantService grantService,
    TimeProvider timeProvider,
    ILogger<EndImpersonationCommandHandler> logger)
    : ICommandHandler<EndImpersonationCommand, EndImpersonationResponse>
{
    public async ValueTask<EndImpersonationResponse> Handle(
        EndImpersonationCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!currentUser.IsAuthenticated())
        {
            throw new UnauthorizedException();
        }

        var claims = currentUser.GetUserClaims()?.ToList()
            ?? throw new UnauthorizedException();

        var actorUserId = claims.FirstOrDefault(c => c.Type == ClaimConstants.ActorSubject)?.Value;
        var actorTenantId = claims.FirstOrDefault(c => c.Type == ClaimConstants.ActorTenant)?.Value;
        var jti = claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value;

        if (string.IsNullOrWhiteSpace(actorUserId) || string.IsNullOrWhiteSpace(actorTenantId))
        {
            // Signed in but no act_sub claim (End called on a non-acting token): client error,
            // must be 4xx not CustomException's default 500.
            throw new CustomException(
                "current session is not an impersonation session",
                errors: null,
                System.Net.HttpStatusCode.BadRequest);
        }

        var impersonatedUserId = currentUser.GetUserId().ToString();
        var impersonatedTenantId = currentUser.GetTenant() ?? string.Empty;

        // Marking the grant ended is the whole job: the JWT hook reads it on the next request.
        // A failure here is logged, not fatal — the short-lived token expires by itself anyway.
        if (!string.IsNullOrWhiteSpace(jti))
        {
            try
            {
                await grantService.MarkEndedByJtiAsync(jti, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex,
                    "Failed to mark impersonation grant ended for jti={Jti}. The token still expires naturally.",
                    jti);
            }
        }

        await securityAudit.ImpersonationEndedAsync(
            actorUserId: actorUserId,
            actorTenantId: actorTenantId,
            targetUserId: impersonatedUserId,
            targetTenantId: impersonatedTenantId,
            clientId: requestContext.ClientId ?? "unknown",
            ct: cancellationToken).ConfigureAwait(false);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Acting session ended: actor {ActorUserId}@{ActorTenant} stepped out of {TargetUserId}@{TargetTenant} jti={Jti}",
                actorUserId, actorTenantId, impersonatedUserId, impersonatedTenantId, jti ?? "<missing>");
        }

        return new EndImpersonationResponse(
            ActorUserId: actorUserId,
            ActorTenantId: actorTenantId,
            ImpersonatedUserId: impersonatedUserId,
            ImpersonatedTenantId: impersonatedTenantId,
            EndedAtUtc: timeProvider.GetUtcNow().UtcDateTime);
    }
}
