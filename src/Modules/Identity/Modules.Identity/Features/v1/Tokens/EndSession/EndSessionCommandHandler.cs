using Boilerplate.BuildingBlocks.Core.Context;
using Boilerplate.Modules.Auditing.Contracts;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Contracts.v1.Tokens.EndSession;
using Mediator;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace Boilerplate.Modules.Identity.Features.v1.Tokens.EndSession;

/// <summary>
/// Ends one device's session server-side. Two ways in, because the client may have either credential
/// and a browser normally has only the second:
///   1. a live access token, whose <c>sid</c> claim names the session row, or
///   2. the refresh token itself, from the request body or the HttpOnly cookie.
///
/// The outcome is never reported. Whether a token matched a live session or nothing at all, the
/// endpoint answers 204 — otherwise logout would be an oracle for probing stolen tokens.
/// </summary>
public sealed class EndSessionCommandHandler : ICommandHandler<EndSessionCommand, Unit>
{
    private readonly ISessionService _sessionService;
    private readonly ICurrentUser _currentUser;
    private readonly ISecurityAudit _securityAudit;
    private readonly IRequestContext _requestContext;

    public EndSessionCommandHandler(
        ISessionService sessionService,
        ICurrentUser currentUser,
        ISecurityAudit securityAudit,
        IRequestContext requestContext)
    {
        _sessionService = sessionService;
        _currentUser = currentUser;
        _securityAudit = securityAudit;
        _requestContext = requestContext;
    }

    public async ValueTask<Unit> Handle(EndSessionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        string? subject = null;
        var revoked = false;

        if (_currentUser.IsAuthenticated() && TryGetSessionId(out var sessionId))
        {
            subject = _currentUser.GetUserId().ToString();
            revoked = await _sessionService.RevokeSessionAsync(
                sessionId, subject, "User logged out", cancellationToken);
        }

        // Fall back to the token itself when there is no usable access token — the browser case,
        // and also the one where the access token outlived its session row.
        if (!revoked && !string.IsNullOrWhiteSpace(command.RefreshToken))
        {
            revoked = await _sessionService.RevokeSessionByRefreshTokenAsync(
                command.RefreshToken, cancellationToken);
        }

        if (revoked)
        {
            await _securityAudit.TokenRevokedAsync(
                subject ?? "unknown",
                _requestContext.ClientId ?? "unknown",
                "UserLoggedOut",
                cancellationToken);
        }

        return Unit.Value;
    }

    /// <summary>
    /// The JWT handler's default inbound map rewrites <c>sid</c> to <see cref="ClaimTypes.Sid"/>,
    /// so both spellings are read — the short form is what the token actually carries.
    /// </summary>
    private bool TryGetSessionId(out Guid sessionId)
    {
        sessionId = Guid.Empty;

        var claims = _currentUser.GetUserClaims();
        var value = claims?
            .FirstOrDefault(c => c.Type == JwtRegisteredClaimNames.Sid || c.Type == ClaimTypes.Sid)?
            .Value;

        return value is not null && Guid.TryParse(value, out sessionId);
    }
}
