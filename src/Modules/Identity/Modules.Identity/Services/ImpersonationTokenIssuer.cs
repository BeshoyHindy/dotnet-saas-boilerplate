using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Constants;
using Boilerplate.Modules.Auditing.Contracts;
using Boilerplate.Modules.Identity.Contracts.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Boilerplate.Modules.Identity.Services;

/// <summary>
/// The one place a token is minted for somebody else's identity. Both surfaces that do it —
/// same-tenant impersonation (<c>/impersonation/start</c>) and the root operator token exchange
/// (<c>/operator/token-exchange</c>) — go through here, so they share a single grant table,
/// a single revocation list (keyed by <c>jti</c>), a single lifetime ceiling and a single audit
/// record shape. Authorization ("may this caller do this at all?") stays in the handlers.
/// </summary>
public interface IImpersonationTokenIssuer
{
    Task<IssuedActingToken> IssueAsync(IssueActingTokenRequest request, CancellationToken ct = default);
}

/// <summary>
/// What to mint and on whose behalf. <c>RequestedMinutes</c>: null → the configured default;
/// larger than the configured max → clamped, never refused.
/// </summary>
public sealed record IssueActingTokenRequest(
    string ActorUserId,
    string? ActorUserName,
    string ActorTenantId,
    string TargetUserId,
    string TargetTenantId,
    string Reason,
    int? RequestedMinutes = null);

public sealed record IssuedActingToken(
    string AccessToken,
    DateTime ExpiresAtUtc,
    string Jti,
    Guid GrantId,
    string TargetUserId,
    string? TargetUserName);

internal sealed class ImpersonationTokenIssuer : IImpersonationTokenIssuer
{
    private readonly IIdentityService _identityService;
    private readonly ITokenService _tokenService;
    private readonly IImpersonationGrantService _grantService;
    private readonly ISecurityAudit _securityAudit;
    private readonly IRequestContext _requestContext;
    private readonly TimeProvider _timeProvider;
    private readonly OperatorExchangeOptions _options;
    private readonly ILogger<ImpersonationTokenIssuer> _logger;

    public ImpersonationTokenIssuer(
        IIdentityService identityService,
        ITokenService tokenService,
        IImpersonationGrantService grantService,
        ISecurityAudit securityAudit,
        IRequestContext requestContext,
        TimeProvider timeProvider,
        IOptions<OperatorExchangeOptions> options,
        ILogger<ImpersonationTokenIssuer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _identityService = identityService;
        _tokenService = tokenService;
        _grantService = grantService;
        _securityAudit = securityAudit;
        _requestContext = requestContext;
        _timeProvider = timeProvider;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IssuedActingToken> IssueAsync(IssueActingTokenRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var targetClaimsResult = await _identityService
            .BuildClaimsForUserAsync(request.TargetUserId, request.TargetTenantId, ct)
            .ConfigureAwait(false);

        if (targetClaimsResult is null)
        {
            throw new NotFoundException("target user not found");
        }

        var (subject, claims) = targetClaimsResult.Value;
        var targetUserName = claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value
            ?? claims.FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Name)?.Value;

        // Strip the auto-generated jti from BuildClaimsForUserAsync and inject our own, so the persisted
        // grant row and the issued JWT share the same jti — that equality *is* the revocation list.
        var jti = Guid.NewGuid().ToString("N");
        var actingClaims = claims
            .Where(c => c.Type != JwtRegisteredClaimNames.Jti)
            .Concat(
            [
                new Claim(JwtRegisteredClaimNames.Jti, jti),
                // RFC 8693 actor claims so the issued token carries who is really acting.
                new Claim(ClaimConstants.ActorSubject, request.ActorUserId),
                new Claim(ClaimConstants.ActorTenant, request.ActorTenantId)
            ])
            .ToList();

        // One ceiling for every acting token. Clamped here rather than only in a validator so a
        // future caller that bypasses validation still cannot escape it.
        var minutes = Math.Clamp(
            request.RequestedMinutes ?? _options.DefaultMinutes,
            1,
            _options.MaxMinutes);

        var startedAtUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var (accessToken, expiresAt) = await _tokenService
            .IssueAccessOnlyAsync(subject, actingClaims, TimeSpan.FromMinutes(minutes), ct)
            .ConfigureAwait(false);

        // Persist the grant AFTER issuance so a failed issue leaves no orphan grant. CreateAsync primes
        // the cache so the JWT validation hook sees status=Active on the next request without a DB hit.
        var grant = await _grantService.CreateAsync(new CreateGrantInput(
            Jti: jti,
            ActorUserId: request.ActorUserId,
            ActorUserName: request.ActorUserName,
            ActorTenantId: request.ActorTenantId,
            ImpersonatedUserId: subject,
            ImpersonatedUserName: targetUserName,
            ImpersonatedTenantId: request.TargetTenantId,
            Reason: request.Reason,
            StartedAtUtc: startedAtUtc,
            ExpiresAtUtc: expiresAt,
            ClientId: _requestContext.ClientId,
            IpAddress: _requestContext.IpAddress,
            UserAgent: _requestContext.UserAgent), ct).ConfigureAwait(false);

        // The audit row lands in the *ambient* tenant — i.e. the caller's own tenant (root for an
        // exchange), which is exactly where the operator can find it. The target tenant is recorded
        // in the claims snapshot, so the impersonated tenant's own audit view is not polluted.
        await _securityAudit.ImpersonationStartedAsync(
            actorUserId: request.ActorUserId,
            actorTenantId: request.ActorTenantId,
            targetUserId: subject,
            targetTenantId: request.TargetTenantId,
            clientId: _requestContext.ClientId ?? "unknown",
            ip: _requestContext.IpAddress ?? "unknown",
            userAgent: _requestContext.UserAgent ?? "unknown",
            reason: request.Reason,
            jti: jti,
            expiresAtUtc: expiresAt,
            ct: ct).ConfigureAwait(false);

        _logger.LogWarning(
            "Acting token issued: actor {ActorUserId}@{ActorTenant} -> target {TargetUserId}@{TargetTenant} jti={Jti} expires={ExpiresAtUtc:o}",
            request.ActorUserId, request.ActorTenantId, subject, request.TargetTenantId, jti, expiresAt);

        return new IssuedActingToken(
            AccessToken: accessToken,
            ExpiresAtUtc: expiresAt,
            Jti: jti,
            GrantId: grant.Id,
            TargetUserId: subject,
            TargetUserName: targetUserName);
    }
}
