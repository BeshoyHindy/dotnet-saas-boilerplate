namespace Integration.Tests.Infrastructure.TenantSweep;

/// <summary>
/// The shape of the swept surface, written down route by route.
///
/// <para><b>Why sets and not floors.</b> "More than twenty routes were swept" is true of a sweep that
/// quietly lost eight of them, and true of one that gained a route nobody looked at. Both are the
/// failure this file exists to prevent. <c>ExemptFromTenantSweep</c> is generic over
/// <see cref="Microsoft.AspNetCore.Builder.IEndpointConventionBuilder"/>, so one call on a
/// <c>MapGroup</c> would exempt every endpoint in that group — including every endpoint added to it
/// afterwards, silently, forever. Pinning the names is what makes that visible the moment it
/// happens.</para>
///
/// <para><b>Adding an endpoint means updating the set on purpose.</b> The failure names exactly which
/// route appeared or disappeared; add it here in the same commit, and the diff then shows a reviewer
/// that the API's tenant-scoped surface changed.</para>
/// </summary>
public static class TenantSweepShape
{
    /// <summary>
    /// Resource routes swept with tenant B's id and a tenant-A token. Losing one silently is the
    /// regression this pins; gaining one is a new endpoint that must be reviewed for isolation.
    /// </summary>
    public static IReadOnlySet<string> SweptResourceRoutes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "DELETE api/v{version:apiVersion}/files/{id:guid}",
        "DELETE api/v{version:apiVersion}/identity/groups/{groupId:guid}/members/{userId}",
        "DELETE api/v{version:apiVersion}/identity/groups/{id:guid}",
        "DELETE api/v{version:apiVersion}/identity/roles/{id:guid}",
        "DELETE api/v{version:apiVersion}/identity/sessions/{sessionId:guid}",
        "DELETE api/v{version:apiVersion}/identity/users/{id:guid}",
        "DELETE api/v{version:apiVersion}/identity/users/{userId:guid}/sessions/{sessionId:guid}",
        "GET api/v{version:apiVersion}/audits/by-correlation/{correlationId}",
        "GET api/v{version:apiVersion}/audits/by-trace/{traceId}",
        "GET api/v{version:apiVersion}/audits/{id:guid}",
        "GET api/v{version:apiVersion}/files/{id:guid}",
        "GET api/v{version:apiVersion}/files/{id:guid}/url",
        "GET api/v{version:apiVersion}/identity/groups/{groupId:guid}/members",
        "GET api/v{version:apiVersion}/identity/groups/{id:guid}",
        "GET api/v{version:apiVersion}/identity/roles/{id:guid}",
        "GET api/v{version:apiVersion}/identity/users/{id:guid}",
        "GET api/v{version:apiVersion}/identity/users/{id:guid}/roles",
        "GET api/v{version:apiVersion}/identity/users/{userId:guid}/sessions",
        "GET api/v{version:apiVersion}/identity/users/{userId}/groups",
        "GET api/v{version:apiVersion}/identity/{id:guid}/permissions",
        "PATCH api/v{version:apiVersion}/files/{id:guid}/visibility",
        "PATCH api/v{version:apiVersion}/identity/users/{id:guid}",
        "POST api/v{version:apiVersion}/files/{id:guid}/finalize",
        "POST api/v{version:apiVersion}/files/{id:guid}/restore",
        "POST api/v{version:apiVersion}/identity/groups/{groupId:guid}/members",
        "POST api/v{version:apiVersion}/identity/impersonation/grants/{id:guid}/revoke",
        "POST api/v{version:apiVersion}/identity/users/{id:guid}/confirm-email",
        "POST api/v{version:apiVersion}/identity/users/{id:guid}/resend-confirmation-email",
        "POST api/v{version:apiVersion}/identity/users/{id:guid}/roles",
        "POST api/v{version:apiVersion}/identity/users/{userId:guid}/sessions/revoke-all",
        "POST api/v{version:apiVersion}/notifications/{id:guid}/read",
        "PUT api/v{version:apiVersion}/identity/groups/{id:guid}",
        "PUT api/v{version:apiVersion}/identity/{id}/permissions",
    };

    /// <summary>
    /// Resource routes whose permission is flagged <c>IsRoot</c>: a tenant admin must be refused, and
    /// the root-token pass is what sweeps them.
    /// </summary>
    public static IReadOnlySet<string> RootOnlyResourceRoutes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "GET api/v{version:apiVersion}/tenants/{id}/status",
        "GET api/v{version:apiVersion}/tenants/{tenantId}/provisioning",
        "POST api/v{version:apiVersion}/tenants/{id}/activation",
        "POST api/v{version:apiVersion}/tenants/{id}/adjust-validity",
        "POST api/v{version:apiVersion}/tenants/{id}/renew",
        "POST api/v{version:apiVersion}/tenants/{tenantId}/provisioning/retry",
    };

    /// <summary>Collections on the versioned API, searched for the other tenant's marker.</summary>
    public static IReadOnlySet<string> CollectionRoutes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "GET api/v{version:apiVersion}/audits/",
        "GET api/v{version:apiVersion}/audits/exceptions",
        "GET api/v{version:apiVersion}/audits/security",
        "GET api/v{version:apiVersion}/audits/summary",
        "GET api/v{version:apiVersion}/files/mine",
        "GET api/v{version:apiVersion}/files/shared",
        "GET api/v{version:apiVersion}/files/trash",
        "GET api/v{version:apiVersion}/identity/groups",
        "GET api/v{version:apiVersion}/identity/impersonation/grants",
        "GET api/v{version:apiVersion}/identity/permissions",
        "GET api/v{version:apiVersion}/identity/permissions/catalog",
        "GET api/v{version:apiVersion}/identity/profile",
        "GET api/v{version:apiVersion}/identity/roles",
        "GET api/v{version:apiVersion}/identity/sessions",
        "GET api/v{version:apiVersion}/identity/sessions/me",
        "GET api/v{version:apiVersion}/identity/users",
        "GET api/v{version:apiVersion}/identity/users/search",
        "GET api/v{version:apiVersion}/notifications/",
        "GET api/v{version:apiVersion}/notifications/unread-count",
        "GET api/v{version:apiVersion}/tenants/",
        "GET api/v{version:apiVersion}/tenants/me/status",
        "GET api/v{version:apiVersion}/tenants/migrations",
        "GET api/v{version:apiVersion}/tenants/theme",
        "GET api/v{version:apiVersion}/tenants/{tenant}/auth/confirm-email",
    };

    /// <summary>
    /// Every route carrying <c>[TenantSweepExempt]</c>. A group-level exemption would show up here as
    /// a burst of new names at once — which is the whole point of pinning it.
    /// </summary>
    public static IReadOnlySet<string> ExemptRoutes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "* /jobs/{**path}",
        "* {*path:nonfile}",
        "GET /openapi/{documentName}.json",
        "GET /scalar/favicon.svg",
        "GET /scalar/scalar.aspnetcore.js",
        "GET /scalar/scalar.js",
        "GET /scalar/{documentName?}",
    };
}
