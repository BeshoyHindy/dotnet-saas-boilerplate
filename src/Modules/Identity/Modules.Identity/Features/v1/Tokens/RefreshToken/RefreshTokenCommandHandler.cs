using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.Modules.Auditing.Contracts;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.Modules.Identity.Contracts.v1.Tokens.RefreshToken;
using Mediator;
using Microsoft.Extensions.Logging;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Boilerplate.Modules.Identity.Features.v1.Tokens.RefreshToken;

public sealed class RefreshTokenCommandHandler
    : ICommandHandler<RefreshTokenCommand, RefreshTokenCommandResponse>
{
    private readonly IIdentityService _identityService;
    private readonly ITokenService _tokenService;
    private readonly ISecurityAudit _securityAudit;
    private readonly IRequestContext _requestContext;
    private readonly ISessionService _sessionService;
    private readonly ILogger<RefreshTokenCommandHandler> _logger;

    public RefreshTokenCommandHandler(
        IIdentityService identityService,
        ITokenService tokenService,
        ISecurityAudit securityAudit,
        IRequestContext requestContext,
        ISessionService sessionService,
        ILogger<RefreshTokenCommandHandler> logger)
    {
        _identityService = identityService;
        _tokenService = tokenService;
        _securityAudit = securityAudit;
        _requestContext = requestContext;
        _sessionService = sessionService;
        _logger = logger;
    }

    public async ValueTask<RefreshTokenCommandResponse> Handle(
        RefreshTokenCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var clientId = _requestContext.ClientId;

        // Rotation is the authority: it resolves the session, proves the token is the live one and
        // spends it, all inside the tenant's query filter. Nothing downstream re-validates the token.
        var rotation = await _sessionService.RotateRefreshTokenAsync(request.RefreshToken, cancellationToken);

        if (rotation.Status != SessionRotationStatus.Rotated)
        {
            await _securityAudit.TokenRevokedAsync(
                rotation.UserId ?? "unknown", clientId!, RevocationReason(rotation.Status), cancellationToken);

            // One message for every failure mode: the caller learns only that the token is no good,
            // never whether it was unknown, expired, replayed or beaten by a concurrent refresh.
            throw new UnauthorizedException("Invalid refresh token.");
        }

        var validated = await _identityService.BuildClaimsForRefreshAsync(rotation.UserId!, cancellationToken);
        if (validated is null)
        {
            await _securityAudit.TokenRevokedAsync(rotation.UserId!, clientId!, "UserNotFound", cancellationToken);
            throw new UnauthorizedException("Invalid refresh token.");
        }

        var (subject, claims) = validated.Value;

        // Optionally, cross-check the provided access token subject
        var handler = new JwtSecurityTokenHandler();
        JwtSecurityToken? parsedAccessToken = null;
        try
        {
            parsedAccessToken = handler.ReadJwtToken(request.Token);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to parse access token during refresh; relying on refresh-token validation only");
        }

        if (parsedAccessToken is not null)
        {
            var accessTokenSubject = parsedAccessToken.Claims
                .FirstOrDefault(c => c.Type == ClaimTypes.NameIdentifier)?.Value
                ?? parsedAccessToken.Subject;

            if (!string.IsNullOrEmpty(accessTokenSubject) &&
                !string.Equals(accessTokenSubject, subject, StringComparison.Ordinal))
            {
                await _securityAudit.TokenRevokedAsync(subject, clientId!, "RefreshTokenSubjectMismatch", cancellationToken);
                throw new UnauthorizedException("Access token subject mismatch.");
            }
        }

        // Audit previous token revocation by rotation (no raw tokens)
        await _securityAudit.TokenRevokedAsync(subject, clientId!, "RefreshTokenRotated", cancellationToken);

        // `sid` is the session row's id, which rotation deliberately leaves alone — the device keeps
        // one identity for its whole life, so consumers can correlate across refreshes.
        var (accessToken, accessTokenExpiresAt) = await _tokenService.IssueAccessOnlyAsync(
            subject,
            claims.Append(new Claim(JwtRegisteredClaimNames.Sid, rotation.SessionId.ToString())),
            lifetime: null,
            cancellationToken);

        // Audit the newly issued token with a fingerprint
        var fingerprint = TokenFingerprint.Sha256Short(accessToken);
        await _securityAudit.TokenIssuedAsync(
            userId: subject,
            userName: claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value ?? string.Empty,
            clientId: clientId!,
            tokenFingerprint: fingerprint,
            expiresUtc: accessTokenExpiresAt,
            ct: cancellationToken);

        return new RefreshTokenCommandResponse(
            Token: accessToken,
            RefreshToken: rotation.RefreshToken!,
            RefreshTokenExpiryTime: rotation.RefreshTokenExpiresAt);
    }

    private static string RevocationReason(SessionRotationStatus status) => status switch
    {
        SessionRotationStatus.Reused => "RefreshTokenReuseDetected",
        SessionRotationStatus.Revoked => "SessionRevoked",
        SessionRotationStatus.Expired => "RefreshTokenExpired",
        SessionRotationStatus.SecurityStampChanged => "SecurityStampChanged",
        SessionRotationStatus.Superseded => "RefreshTokenSuperseded",
        _ => "InvalidRefreshToken",
    };
}
