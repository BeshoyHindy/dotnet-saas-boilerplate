using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.Modules.Auditing.Contracts;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Impersonation.EndImpersonation;
using Mediator;
using Microsoft.Extensions.Logging;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;

namespace Boilerplate.Modules.Identity.Features.v1.Impersonation.EndImpersonation;

public sealed class EndImpersonationCommandHandler
    : ICommandHandler<EndImpersonationCommand, EndImpersonationResponse>
{
    private readonly IIdentityService _identityService;
    private readonly ITokenService _tokenService;
    private readonly ISecurityAudit _securityAudit;
    private readonly ICurrentUser _currentUser;
    private readonly IRequestContext _requestContext;
    private readonly IImpersonationGrantService _grantService;
    private readonly ILogger<EndImpersonationCommandHandler> _logger;

    public EndImpersonationCommandHandler(
        IIdentityService identityService,
        ITokenService tokenService,
        ISecurityAudit securityAudit,
        ICurrentUser currentUser,
        IRequestContext requestContext,
        IImpersonationGrantService grantService,
        ILogger<EndImpersonationCommandHandler> logger)
    {
        _identityService = identityService;
        _tokenService = tokenService;
        _securityAudit = securityAudit;
        _currentUser = currentUser;
        _requestContext = requestContext;
        _grantService = grantService;
        _logger = logger;
    }

    public async ValueTask<EndImpersonationResponse> Handle(
        EndImpersonationCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!_currentUser.IsAuthenticated())
        {
            throw new UnauthorizedException();
        }

        var claims = _currentUser.GetUserClaims()?.ToList()
            ?? throw new UnauthorizedException();

        var actorUserId = claims.FirstOrDefault(c => c.Type == ClaimConstants.ActorSubject)?.Value;
        var actorTenantId = claims.FirstOrDefault(c => c.Type == ClaimConstants.ActorTenant)?.Value;
        var jti = claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Jti)?.Value;

        if (string.IsNullOrWhiteSpace(actorUserId) || string.IsNullOrWhiteSpace(actorTenantId))
        {
            // Signed in but no act_sub claim (End called on a non-impersonation token): client error,
            // must be 4xx not CustomException's default 500.
            throw new CustomException(
                "current session is not an impersonation session",
                errors: null,
                System.Net.HttpStatusCode.BadRequest);
        }

        var impersonatedUserId = _currentUser.GetUserId().ToString();
        var impersonatedTenantId = _currentUser.GetTenant() ?? string.Empty;

        // Mark grant ended BEFORE issuing actor tokens so a racing JWT-hook request sees "ended" (safer than the reverse).
        // If MarkEnded fails we proceed anyway: the grant expires naturally and the hook treats Unknown states as revoked.
        if (!string.IsNullOrWhiteSpace(jti))
        {
            try
            {
                await _grantService.MarkEndedByJtiAsync(jti, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to mark impersonation grant ended for jti={Jti}. Actor swap will still proceed.",
                    jti);
            }
        }

        var actorClaimsResult = await _identityService
            .BuildClaimsForUserAsync(actorUserId, actorTenantId, cancellationToken);

        if (actorClaimsResult is null)
        {
            throw new NotFoundException("original actor not found");
        }

        var (subject, actorClaims) = actorClaimsResult.Value;

        // Access-only, no refresh: a refresh token is a row in the *actor's* tenant session store,
        // and this request is running inside the impersonated tenant's context — writing there would
        // file the operator's session under the wrong tenant. The actor's own session (if they have
        // one) was never revoked, so stepping out restores them immediately; a cross-app operator
        // re-authenticates when this short-lived token expires. Operator token exchange (#9) is
        // where crossing a tenant boundary gets its proper mechanism.
        var (accessToken, accessTokenExpiresAt) = await _tokenService.IssueAccessOnlyAsync(
            subject, actorClaims, lifetime: null, cancellationToken);

        await _securityAudit.ImpersonationEndedAsync(
            actorUserId: actorUserId,
            actorTenantId: actorTenantId,
            targetUserId: impersonatedUserId,
            targetTenantId: impersonatedTenantId,
            clientId: _requestContext.ClientId ?? "unknown",
            ct: cancellationToken);

        if (_logger.IsEnabled(LogLevel.Information))
        {
            _logger.LogInformation(
                "Impersonation ended: actor {ActorUserId}@{ActorTenant} returned from {TargetUserId}@{TargetTenant} jti={Jti}",
                actorUserId, actorTenantId, impersonatedUserId, impersonatedTenantId, jti ?? "<missing>");
        }

        return new EndImpersonationResponse(accessToken, accessTokenExpiresAt);
    }
}
