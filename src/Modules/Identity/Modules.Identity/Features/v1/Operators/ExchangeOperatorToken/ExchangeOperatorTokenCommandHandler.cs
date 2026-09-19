using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Operators.ExchangeOperatorToken;
using Boilerplate.Modules.Identity.Services;
using Finbuckle.MultiTenant.Abstractions;
using Mediator;
using System.Net;

namespace Boilerplate.Modules.Identity.Features.v1.Operators.ExchangeOperatorToken;

/// <summary>
/// ADR-0002's cross-tenant mechanism. A root operator trades their own token for a short-lived,
/// access-only token whose <c>tenant</c> claim is the target and whose subject is a real user of
/// that tenant — so every downstream permission check runs unchanged against that tenant's data.
/// No refresh token, no session row, no cookie: the operator's own session is untouched, and
/// "exit tenant" is a client-side drop plus a revoke of the grant this creates.
/// </summary>
public sealed class ExchangeOperatorTokenCommandHandler(
    ICurrentUser currentUser,
    IIdentityService identityService,
    IImpersonationTokenIssuer tokenIssuer,
    IMultiTenantStore<AppTenantInfo> tenantStore)
    : ICommandHandler<ExchangeOperatorTokenCommand, OperatorTokenExchangeResponse>
{
    public async ValueTask<OperatorTokenExchangeResponse> Handle(
        ExchangeOperatorTokenCommand request,
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

        // Root-only, independently of the permission gate on the endpoint: the permission is
        // root-scoped in the catalog, but a mis-seeded role in some tenant must still not get here.
        if (!string.Equals(actorTenantId, MultitenancyConstants.Root.Id, StringComparison.Ordinal))
        {
            throw new ForbiddenException("token exchange is restricted to platform operators");
        }

        // No nesting: an exchanged token may not be exchanged again (nor start an impersonation).
        // Otherwise the act_* claims — the only record of who is really acting — would be overwritten.
        var callerClaims = currentUser.GetUserClaims();
        if (callerClaims is not null && callerClaims.Any(c => c.Type == ClaimConstants.ActorSubject))
        {
            throw new CustomException(
                "exit the current tenant before exchanging again",
                errors: null,
                HttpStatusCode.BadRequest);
        }

        if (string.Equals(request.TargetTenantId, MultitenancyConstants.Root.Id, StringComparison.Ordinal))
        {
            throw new CustomException(
                "the operator is already in the root tenant",
                errors: null,
                HttpStatusCode.BadRequest);
        }

        var targetTenant = await tenantStore.GetAsync(request.TargetTenantId).ConfigureAwait(false)
            ?? throw new NotFoundException("target tenant not found");

        // A deactivated tenant is refused rather than entered: the deactivated-tenant guard rejects
        // every request whose resolved tenant is inactive, and an exchanged token resolves to the
        // TARGET tenant — so the token would be dead on arrival. Failing here says so honestly
        // instead of handing over an unusable credential. Reactivate the tenant to inspect it.
        if (!targetTenant.IsActive)
        {
            throw new ForbiddenException("target tenant is deactivated");
        }

        var lookup = await ResolveTargetUserAsync(request, targetTenant, cancellationToken).ConfigureAwait(false);

        var issued = await tokenIssuer.IssueAsync(
            new IssueActingTokenRequest(
                ActorUserId: actorUserId,
                ActorUserName: currentUser.Name,
                ActorTenantId: actorTenantId,
                TargetUserId: lookup.UserId,
                TargetTenantId: request.TargetTenantId,
                Reason: request.Reason,
                RequestedMinutes: request.DurationMinutes),
            cancellationToken).ConfigureAwait(false);

        return new OperatorTokenExchangeResponse(
            AccessToken: issued.AccessToken,
            AccessTokenExpiresAt: issued.ExpiresAtUtc,
            TargetTenantId: request.TargetTenantId,
            TargetUserId: issued.TargetUserId,
            TargetUserName: issued.TargetUserName ?? lookup.UserName,
            ActorUserId: actorUserId,
            ActorTenantId: actorTenantId,
            GrantId: issued.GrantId,
            Jti: issued.Jti);
    }

    private async Task<TenantUserLookup> ResolveTargetUserAsync(
        ExchangeOperatorTokenCommand request,
        AppTenantInfo targetTenant,
        CancellationToken ct)
    {
        var explicitUser = !string.IsNullOrWhiteSpace(request.TargetUserId);

        var lookup = await identityService.FindTenantUserAsync(
            tenantId: request.TargetTenantId,
            userId: explicitUser ? request.TargetUserId : null,
            email: explicitUser ? null : targetTenant.AdminEmail,
            ct: ct).ConfigureAwait(false);

        if (lookup is null)
        {
            throw new NotFoundException(explicitUser
                ? "target user not found in the target tenant"
                : "the target tenant has no user matching its admin email; pass targetUserId explicitly");
        }

        // 409, not 404: the user exists, the operator simply may not borrow this identity. A 401
        // here (what the claim builder would throw) would read as "your own token expired".
        if (!lookup.IsActive)
        {
            throw new CustomException(
                "target user is deactivated",
                errors: null,
                HttpStatusCode.Conflict);
        }

        return lookup;
    }
}
