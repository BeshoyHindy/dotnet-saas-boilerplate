using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Auditing.Contracts;
using Boilerplate.Modules.Identity.Contracts.v1.Operators.IssueJobMonitorAccess;
using Boilerplate.Modules.Identity.Features.v1.Tokens;
using Boilerplate.Modules.Identity.Services;
using Mediator;

namespace Boilerplate.Modules.Identity.Features.v1.Operators.IssueJobMonitorAccess;

/// <summary>
/// Mints the Job monitor credential for a root operator's own session (ADR-0009) and records the
/// issue as a security audit event. The endpoint turns it into the cookie.
/// </summary>
public sealed class IssueJobMonitorAccessCommandHandler(
    ICurrentUser currentUser,
    JobMonitorTokenIssuer issuer,
    ISecurityAudit securityAudit)
    : ICommandHandler<IssueJobMonitorAccessCommand, JobMonitorAccessGrant>
{
    /// <summary>The audit trail's client id for a Job monitor cookie, whatever app asked for it.</summary>
    public const string AuditClientId = "job-monitor";

    public async ValueTask<JobMonitorAccessGrant> Handle(
        IssueJobMonitorAccessCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!currentUser.IsAuthenticated())
        {
            throw new UnauthorizedException();
        }

        // Root-only, independently of the permission gate: Hangfire.View is root-scoped in the
        // catalog, but a mis-seeded role in some tenant must still not get here.
        if (!string.Equals(currentUser.GetTenant(), MultitenancyConstants.Root.Id, StringComparison.Ordinal))
        {
            throw new ForbiddenException("the Job monitor is restricted to platform operators");
        }

        var claims = currentUser.GetUserClaims()?.ToList() ?? throw new UnauthorizedException();

        // The endpoint's DenyWhenActing filter already refuses this; the check stays here too so the
        // credential can never be minted for an identity the caller is only borrowing.
        if (claims.Exists(c => c.Type == ClaimConstants.ActorSubject))
        {
            throw new ForbiddenException("not available while acting on behalf of another user");
        }

        var (token, expiresAtUtc) = issuer.Issue(claims);

        await securityAudit.TokenIssuedAsync(
            userId: currentUser.GetUserId().ToString(),
            userName: currentUser.Name ?? string.Empty,
            clientId: AuditClientId,
            tokenFingerprint: TokenFingerprint.Sha256Short(token),
            expiresUtc: expiresAtUtc,
            ct: cancellationToken).ConfigureAwait(false);

        return new JobMonitorAccessGrant(token, expiresAtUtc);
    }
}
