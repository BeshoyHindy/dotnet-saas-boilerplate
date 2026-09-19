namespace Boilerplate.Modules.Auditing.Contracts;

public interface ISecurityAudit
{
    ValueTask LoginSucceededAsync(string userId, string userName, string clientId, string ip, string userAgent, CancellationToken ct = default);
    ValueTask LoginFailedAsync(string subjectIdOrName, string clientId, string reason, string ip, CancellationToken ct = default);
    ValueTask TokenIssuedAsync(string userId, string userName, string clientId, string tokenFingerprint, DateTime expiresUtc, CancellationToken ct = default);
    ValueTask TokenRevokedAsync(string userId, string clientId, string reason, CancellationToken ct = default);
    /// <summary>
    /// One record for every token minted for somebody else's identity — impersonation and the root
    /// operator token exchange share an issuer. <paramref name="jti"/> joins the row to the
    /// ImpersonationGrant that can revoke it; <paramref name="expiresAtUtc"/> bounds the window.
    /// </summary>
    ValueTask ImpersonationStartedAsync(string actorUserId, string actorTenantId, string targetUserId, string targetTenantId, string clientId, string ip, string userAgent, string reason, string? jti = null, DateTime? expiresAtUtc = null, CancellationToken ct = default);
    ValueTask ImpersonationEndedAsync(string actorUserId, string actorTenantId, string targetUserId, string targetTenantId, string clientId, CancellationToken ct = default);
}