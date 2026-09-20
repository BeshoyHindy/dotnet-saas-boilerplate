using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.BuildingBlocks.Eventing.Outbox;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.Modules.Auditing.Contracts;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.Events;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Tokens.TokenGeneration;
using Mediator;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Boilerplate.Modules.Identity.Features.v1.Tokens.TokenGeneration;

public sealed class GenerateTokenCommandHandler
    : ICommandHandler<GenerateTokenCommand, TokenResponse>
{
    private readonly IIdentityService _identityService;
    private readonly ITokenService _tokenService;
    private readonly ISecurityAudit _securityAudit;
    private readonly IRequestContext _requestContext;
    private readonly IOutboxStore _outboxStore;
    private readonly IMultiTenantContextAccessor<AppTenantInfo> _multiTenantContextAccessor;
    private readonly ISessionService _sessionService;

    public GenerateTokenCommandHandler(
        IIdentityService identityService,
        ITokenService tokenService,
        ISecurityAudit securityAudit,
        IRequestContext requestContext,
        IOutboxStore outboxStore,
        IMultiTenantContextAccessor<AppTenantInfo> multiTenantContextAccessor,
        ISessionService sessionService)
    {
        _identityService = identityService;
        _tokenService = tokenService;
        _securityAudit = securityAudit;
        _requestContext = requestContext;
        _outboxStore = outboxStore;
        _multiTenantContextAccessor = multiTenantContextAccessor;
        _sessionService = sessionService;
    }

    public async ValueTask<TokenResponse> Handle(
        GenerateTokenCommand request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Gather context for auditing
        var ip = _requestContext.IpAddress ?? "unknown";
        var ua = _requestContext.UserAgent ?? "unknown";
        var clientId = _requestContext.ClientId;

        // Validate credentials (includes 2FA verification when the user has it enabled)
        var identityResult = await _identityService
            .ValidateCredentialsAsync(request.Email, request.Password, request.TwoFactorCode, cancellationToken);

        if (identityResult is null)
        {
            // 1) Audit failed login BEFORE throwing
            await _securityAudit.LoginFailedAsync(
                subjectIdOrName: request.Email,
                clientId: clientId!,
                reason: "InvalidCredentials",
                ip: ip,
                ct: cancellationToken);

            throw new UnauthorizedAccessException("Invalid credentials.");
        }

        // Unpack subject + claims
        var (subject, claims) = identityResult.Value;

        // 2) Audit successful login
        await _securityAudit.LoginSucceededAsync(
            userId: subject,
            userName: claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value ?? request.Email,
            clientId: clientId!,
            ip: ip,
            userAgent: ua,
            ct: cancellationToken);

        // The session row IS the refresh token, so it is created BEFORE the access token: its id
        // becomes the `sid` claim, and a failure here fails the login. A login that leaves no
        // session behind cannot be refreshed and cannot be revoked — succeeding would be worse
        // than a 500.
        var session = await _sessionService.CreateSessionAsync(subject, ip, ua, cancellationToken);

        var (accessToken, accessTokenExpiresAt) = await _tokenService.IssueAccessOnlyAsync(
            subject,
            claims.Append(new Claim(JwtRegisteredClaimNames.Sid, session.SessionId.ToString())),
            lifetime: null,
            cancellationToken);

        var token = new TokenResponse(
            AccessToken: accessToken,
            RefreshToken: session.RefreshToken,
            RefreshTokenExpiresAt: session.RefreshTokenExpiresAt,
            AccessTokenExpiresAt: accessTokenExpiresAt);

        // 3) Audit token issuance with a fingerprint (never raw token)
        var fingerprint = TokenFingerprint.Sha256Short(token.AccessToken);
        await _securityAudit.TokenIssuedAsync(
            userId: subject,
            userName: claims.FirstOrDefault(c => c.Type == ClaimTypes.Name)?.Value ?? request.Email,
            clientId: clientId!,
            tokenFingerprint: fingerprint,
            expiresUtc: token.AccessTokenExpiresAt,
            ct: cancellationToken);

        // 4) Enqueue integration event for token generation (sample event for testing eventing)
        var tenantId = _multiTenantContextAccessor.MultiTenantContext?.TenantInfo?.Id;
        var correlationId = Guid.NewGuid().ToString();

        var integrationEvent = new TokenGeneratedIntegrationEvent(
            Id: Guid.NewGuid(),
            OccurredOnUtc: TimeProvider.System.GetUtcNow().UtcDateTime,
            TenantId: tenantId,
            CorrelationId: correlationId,
            Source: "Identity",
            UserId: subject,
            Email: request.Email,
            ClientId: clientId!,
            IpAddress: ip,
            UserAgent: ua,
            TokenFingerprint: fingerprint,
            AccessTokenExpiresAtUtc: token.AccessTokenExpiresAt);

        await _outboxStore.AddAsync(integrationEvent, cancellationToken).ConfigureAwait(false);

        return token;
    }
}