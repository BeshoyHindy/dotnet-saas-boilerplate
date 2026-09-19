using System.Net.Http.Json;
using System.Text.Json;
using Boilerplate.Modules.Notifications.Data;
using Boilerplate.Modules.Notifications.Domain;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Microsoft.Extensions.DependencyInjection;

namespace Integration.Tests.Infrastructure.TenantSweep;

/// <summary>
/// The kinds of resource the sweep knows how to create. One value per <see cref="ResourceParameter"/>
/// registry key, so that adding an endpoint under an existing noun costs nothing and adding a new
/// noun fails the coverage test with the name of the entry to write.
/// </summary>
public enum ResourceKind
{
    User,
    Role,
    Group,
    Session,
    File,
    Notification,
    Audit,
    AuditCorrelation,
    AuditTrace,
    ImpersonationGrant,
    Tenant,
}

/// <summary>
/// Everything the sweep seeded inside one tenant. Each value is a live row in that tenant and
/// nowhere else, which is what makes "tenant A asking for this id must 404" a real question.
///
/// <see cref="Marker"/> is a per-tenant nonsense string stamped into every seeded row's human-
/// readable fields (user e-mail, role and group name, file name, notification title). The list half
/// of the sweep looks for it in response bodies, so a leak is caught even on an endpoint nobody
/// wrote a registry entry for — the marker, not the registry, is what proves a list is clean.
/// </summary>
public sealed class SeededTenant
{
    public required string TenantId { get; init; }
    public required string AdminEmail { get; init; }
    public required string Marker { get; init; }
    public required HttpClient AdminClient { get; init; }
    public required IReadOnlyDictionary<ResourceKind, string> Ids { get; init; }

    public string this[ResourceKind kind] => Ids.TryGetValue(kind, out var id)
        ? id
        : throw new InvalidOperationException(
            $"The sweep did not seed a {kind} for tenant {TenantId}; TenantSweepSeeder must create one.");
}

/// <summary>
/// Maps a route's <see cref="ResourceParameter.RegistryKey"/> (preceding literal segment + parameter
/// name) onto the resource the sweep substitutes there.
///
/// This is the registry the coverage test enforces: a route with a resource parameter whose key is
/// absent here, and which carries no <c>[TenantSweepExempt]</c>, fails the sweep with a message
/// naming the key to add. That is how "a newly added endpoint is covered automatically" is true
/// rather than aspirational.
/// </summary>
public static class TenantSweepRegistry
{
    public static IReadOnlyDictionary<string, ResourceKind> ByRouteKey { get; } =
        new Dictionary<string, ResourceKind>(StringComparer.Ordinal)
        {
            // Identity — users
            ["users/id"] = ResourceKind.User,
            ["users/userId"] = ResourceKind.User,
            ["members/userId"] = ResourceKind.User,

            // Identity — roles. The role-permission routes hang off the module root
            // (api/v1/identity/{id}/permissions), so their preceding literal is "identity".
            ["roles/id"] = ResourceKind.Role,
            ["identity/id"] = ResourceKind.Role,

            // Identity — groups, sessions, impersonation grants
            ["groups/id"] = ResourceKind.Group,
            ["groups/groupId"] = ResourceKind.Group,
            ["sessions/sessionId"] = ResourceKind.Session,
            ["grants/id"] = ResourceKind.ImpersonationGrant,

            // Files
            ["files/id"] = ResourceKind.File,

            // Notifications
            ["notifications/id"] = ResourceKind.Notification,

            // Auditing — the by-correlation / by-trace lookups take the audit row's own
            // correlation and trace ids, which are server-minted per request.
            ["audits/id"] = ResourceKind.Audit,
            ["by-correlation/correlationId"] = ResourceKind.AuditCorrelation,
            ["by-trace/traceId"] = ResourceKind.AuditTrace,

            // Multitenancy — the tenant catalog itself. Root-only routes, but the sweep still runs
            // them with a root token to prove root has no override left (ADR-0002).
            ["tenants/id"] = ResourceKind.Tenant,
            ["tenants/tenantId"] = ResourceKind.Tenant,
        };
}

/// <summary>
/// Request bodies for the routes whose validators would 400 before the handler ever looks the
/// resource up. Without these the sweep would be vacuous on every write verb: a 400 is not a 404,
/// and proving "it didn't reach the row" is worthless if nothing could have reached it.
///
/// Keyed by "<c>METHOD template</c>". The factory receives the route values the sweep substituted,
/// so a body that has to echo the id in the payload (the role-permissions PUT compares them and
/// 400s on mismatch) echoes the id actually being probed.
/// </summary>
public static class TenantSweepBodies
{
    /// <summary>
    /// What a body factory is allowed to know: the ids the sweep substituted into the route, and the
    /// tenant whose token is making the call. The second matters — a members-add probe has to name a
    /// user the <i>caller</i> owns, or the request fails validation before the group is ever looked up
    /// and the probe proves nothing about the group.
    /// </summary>
    /// <param name="RouteValues">Parameter name → the id the sweep put in the path.</param>
    /// <param name="Caller">The tenant whose token is sending the request.</param>
    public sealed record BodyContext(IReadOnlyDictionary<string, string> RouteValues, SeededTenant Caller);

    public static IReadOnlyDictionary<string, Func<BodyContext, object?>> ByEndpoint { get; } =
        new Dictionary<string, Func<BodyContext, object?>>(StringComparer.Ordinal)
        {
            ["PATCH api/v{version:apiVersion}/files/{id:guid}/visibility"] =
                _ => new { visibility = 0 },

            ["PATCH api/v{version:apiVersion}/identity/users/{id:guid}"] =
                ctx => new { userId = ctx.RouteValues["id"], activateUser = true },

            ["POST api/v{version:apiVersion}/identity/users/{id:guid}/roles"] =
                ctx => new { userId = ctx.RouteValues["id"], userRoles = Array.Empty<object>() },

            // The caller's own user, deliberately: the question is whether the GROUP in the path is
            // reachable, so everything else in the request has to be unimpeachable.
            ["POST api/v{version:apiVersion}/identity/groups/{groupId:guid}/members"] =
                ctx => new { userIds = new[] { ctx.Caller[ResourceKind.User] } },

            ["PUT api/v{version:apiVersion}/identity/groups/{id:guid}"] =
                _ => new { name = "sweep-probe", description = "sweep", isDefault = false, roleIds = Array.Empty<string>() },

            ["PUT api/v{version:apiVersion}/identity/{id}/permissions"] =
                ctx => new { roleId = ctx.RouteValues["id"], permissions = Array.Empty<string>() },

            ["POST api/v{version:apiVersion}/tenants/{id}/activation"] =
                ctx => new { tenantId = ctx.RouteValues["id"], isActive = true },

            ["POST api/v{version:apiVersion}/tenants/{id}/adjust-validity"] =
                ctx => new { tenantId = ctx.RouteValues["id"], validUpto = DateTime.UtcNow.AddYears(1) },

            ["POST api/v{version:apiVersion}/tenants/{id}/renew"] =
                ctx => new { tenantId = ctx.RouteValues["id"], months = 1 },
        };

    public static object? For(
        string method,
        string template,
        IReadOnlyDictionary<string, string> routeValues,
        SeededTenant caller)
    {
        return ByEndpoint.TryGetValue($"{method} {template}", out var factory)
            ? factory(new BodyContext(routeValues, caller))
            : null;
    }
}

/// <summary>
/// The two places where the sweep's headline rule — "another tenant's id must answer 404" — is the
/// wrong question, written down with the reason rather than quietly skipped.
/// </summary>
public static class TenantSweepExceptions
{
    /// <summary>
    /// Routes whose path parameter is a <i>filter</i>, not a row id: the resource they address is a
    /// collection, and the honest answer to "show me the rows matching this value" when none are
    /// visible is an empty 200, not a 404. Inventing a 404 would mean the endpoint could distinguish
    /// "no rows" from "no rows you may see", which is the leak we are preventing, backwards.
    ///
    /// These are still swept — harder, if anything: the response must contain none of tenant B's
    /// markers or ids, which is a statement about the body and not just the status line.
    /// </summary>
    public static IReadOnlyDictionary<string, string> CollectionShapedRoutes { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GET api/v{version:apiVersion}/audits/by-correlation/{correlationId}"] =
                "a correlation id is a filter over the audit trail, not an addressable row",
            ["GET api/v{version:apiVersion}/audits/by-trace/{traceId}"] =
                "a trace id is a filter over the audit trail, not an addressable row",
            ["GET api/v{version:apiVersion}/identity/users/{userId:guid}/sessions"] =
                "a user's sessions are a sub-collection; an unknown user simply has none visible",
            ["POST api/v{version:apiVersion}/identity/users/{userId:guid}/sessions/revoke-all"] =
                "a bulk revoke over a sub-collection; it reports how many rows it touched, and for " +
                "another tenant's user that count must be zero",
        };

    /// <summary>
    /// Resource kinds that are platform-wide by construction, so the "root gets 404 too" rule does not
    /// apply to them — there is no tenant boundary to cross.
    ///
    /// <c>ImpersonationGrant</c> is an <c>IGlobalEntity</c> on purpose: it is the audit record of an
    /// operator acting as someone, and revoking one is the kill switch for an impersonation already in
    /// flight. A platform operator who could not reach it could not stop it. Tenant admins still get
    /// 404 for another tenant's grant — that part the sweep does enforce.
    ///
    /// <c>Tenant</c> is the catalog itself, and every route that takes one is root-only anyway.
    /// </summary>
    public static IReadOnlySet<ResourceKind> PlatformWideKinds { get; } =
        new HashSet<ResourceKind> { ResourceKind.ImpersonationGrant, ResourceKind.Tenant };
}

/// <summary>
/// Creates one live row of every <see cref="ResourceKind"/> inside a tenant, through the same public
/// API a client would use wherever that is possible — a seeded row that was made some other way is
/// a row the endpoints might not agree exists.
/// </summary>
internal static class TenantSweepSeeder
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<IReadOnlyDictionary<ResourceKind, string>> SeedAsync(
        AppWebApplicationFactory factory,
        AuthHelper auth,
        HttpClient adminClient,
        string tenantId,
        string marker)
    {
        var ids = new Dictionary<ResourceKind, string>();

        var user = await SeedUserAsync(factory, adminClient, tenantId, marker);
        ids[ResourceKind.User] = user.UserId;

        ids[ResourceKind.Role] = await SeedRoleAsync(adminClient, marker);
        ids[ResourceKind.Group] = await SeedGroupAsync(adminClient, marker);
        ids[ResourceKind.File] = await SeedFileAsync(adminClient, marker);
        // The inbox is per-user and the sweep calls with the ADMIN's token, so the notification has
        // to belong to the admin or the positive control 404s for the right reason and the wrong one.
        var adminUserId = await CurrentUserIdAsync(adminClient);
        ids[ResourceKind.Notification] = await SeedNotificationAsync(factory, tenantId, adminUserId, marker);
        ids[ResourceKind.ImpersonationGrant] = await SeedImpersonationGrantAsync(adminClient, tenantId, user.UserId);
        ids[ResourceKind.Session] = await SeedSessionAsync(auth, adminClient, user, tenantId);
        ids[ResourceKind.Tenant] = tenantId;

        var audit = await AuditingProbeAsync(adminClient);
        ids[ResourceKind.Audit] = audit.Id;
        ids[ResourceKind.AuditCorrelation] = audit.CorrelationId;
        ids[ResourceKind.AuditTrace] = audit.TraceId;

        return ids;
    }

    /// <summary>The id of the user whose token <paramref name="client"/> carries.</summary>
    private static async Task<string> CurrentUserIdAsync(HttpClient client)
    {
        using var response = await client.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"reading the caller's profile failed: {await response.Content.ReadAsStringAsync()}");

        var profile = await response.Content.ReadFromJsonAsync<ProfileDto>(Json);
        return profile!.Id;
    }

    /// <summary>A confirmed, loginable non-admin user carrying the tenant's marker in its e-mail.</summary>
    public static async Task<TenantFixture.TestUser> SeedUserAsync(
        AppWebApplicationFactory factory, HttpClient adminClient, string tenantId, string marker)
    {
        return await TenantFixture.RegisterAndConfirmUserAsync(factory, adminClient, tenantId, $"sweep{marker}");
    }

    private static async Task<string> SeedRoleAsync(HttpClient adminClient, string marker)
    {
        using var response = await adminClient.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/roles",
            new { id = string.Empty, name = $"sweep-role-{marker}", description = $"sweep {marker}" });
        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"seeding a role failed: {await response.Content.ReadAsStringAsync()}");

        var dto = await response.Content.ReadFromJsonAsync<IdDto>(Json);
        return dto!.Id;
    }

    private static async Task<string> SeedGroupAsync(HttpClient adminClient, string marker)
    {
        using var response = await adminClient.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/groups",
            new { name = $"sweep-group-{marker}", description = $"sweep {marker}", isDefault = false });
        ((int)response.StatusCode).ShouldBeInRange(
            200, 299,
            $"seeding a group failed: {await response.Content.ReadAsStringAsync()}");

        var dto = await response.Content.ReadFromJsonAsync<IdDto>(Json);
        return dto!.Id;
    }

    /// <summary>
    /// A FileAsset in <c>PendingUpload</c>. Requesting the upload URL is what creates the row, so the
    /// sweep gets a real, addressable file without pushing bytes through MinIO — the presigned PUT is
    /// the client's job and is not what tenant isolation turns on.
    /// </summary>
    private static async Task<string> SeedFileAsync(HttpClient adminClient, string marker)
    {
        using var response = await adminClient.PostAsJsonAsync(
            "/api/v1/files/upload-url",
            new
            {
                ownerType = "MyFiles",
                ownerId = (Guid?)null,
                fileName = $"sweep-{marker}.pdf",
                contentType = "application/pdf",
                sizeBytes = 256,
                visibility = 1,
                category = "Document",
            });
        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"seeding a file failed: {await response.Content.ReadAsStringAsync()}");

        var dto = await response.Content.ReadFromJsonAsync<PresignedDto>(Json);
        return dto!.FileAssetId.ToString();
    }

    /// <summary>
    /// Notifications are written by the integration-event handler, not by an endpoint, so this one
    /// row goes in through the tenant's own DbContext under <see cref="ITenantScope"/> — the same
    /// door the handler uses.
    /// </summary>
    private static async Task<string> SeedNotificationAsync(
        AppWebApplicationFactory factory, string tenantId, string userId, string marker)
    {
        var scope = factory.Services.GetRequiredService<ITenantScope>();

        return await scope.RunAsync(tenantId, async (services, ct) =>
        {
            var db = services.GetRequiredService<NotificationsDbContext>();
            var notification = Notification.Create(
                userId,
                "sweep.probe",
                $"sweep-notification-{marker}",
                $"seeded for the cross-tenant sweep ({marker})",
                link: null,
                source: "TenantSweep",
                metadata: null);

            db.Notifications.Add(notification);
            await db.SaveChangesAsync(ct);
            return notification.Id.ToString();
        }, CancellationToken.None);
    }

    /// <summary>
    /// An impersonation grant, created through the endpoint so it is audited and shaped exactly like
    /// a real one. <c>ImpersonationGrant</c> is an <see cref="Boilerplate.BuildingBlocks.Core.Domain.IGlobalEntity"/>
    /// — deliberately not tenant-filtered — which makes the revoke route one of the few places where
    /// isolation has to come from the handler rather than from the query filter. That is precisely
    /// why the sweep seeds one.
    /// </summary>
    private static async Task<string> SeedImpersonationGrantAsync(
        HttpClient adminClient, string tenantId, string userId)
    {
        using var started = await adminClient.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/impersonation/start",
            new
            {
                targetUserId = userId,
                targetTenantId = tenantId,
                reason = "cross-tenant sweep seed",
                durationMinutes = 10,
            });
        started.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"seeding an impersonation grant failed: {await started.Content.ReadAsStringAsync()}");

        // The start response carries the acting token, not the grant id; the grants list is the
        // only public way to learn it — which is also the route the sweep will probe.
        using var grants = await adminClient.GetAsync(
            $"{TestConstants.IdentityBasePath}/impersonation/grants?take=100");
        grants.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"listing impersonation grants failed: {await grants.Content.ReadAsStringAsync()}");

        var rows = await grants.Content.ReadFromJsonAsync<List<GrantRow>>(Json);
        var grant = rows!.FirstOrDefault(g =>
                string.Equals(g.ImpersonatedUserId, userId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"no impersonation grant materialised for user {userId} in tenant {tenantId}");

        return grant.Id.ToString();
    }

    /// <summary>
    /// A UserSession row: logging the seeded user in mints one. Read back through the tenant
    /// sessions list so the id is the one the API itself would hand a caller.
    /// </summary>
    private static async Task<string> SeedSessionAsync(
        AuthHelper auth, HttpClient adminClient, TenantFixture.TestUser user, string tenantId)
    {
        await TenantFixture.GetTokenWithRetryAsync(auth, user.Email, user.Password, tenantId);

        using var response = await adminClient.GetAsync(
            $"{TestConstants.IdentityBasePath}/sessions?pageNumber=1&pageSize=200");
        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"listing sessions failed: {await response.Content.ReadAsStringAsync()}");

        var page = await response.Content.ReadFromJsonAsync<PagedIds>(Json);
        var session = page!.Items.FirstOrDefault(s => string.Equals(s.UserId, user.UserId, StringComparison.Ordinal))
            ?? page.Items.FirstOrDefault()
            ?? throw new InvalidOperationException($"no UserSession materialised in tenant {tenantId}");

        return session.Id;
    }

    /// <summary>
    /// Audit rows are written asynchronously by a background drain, and their correlation/trace ids
    /// are minted server-side per request — so the only honest way to get one is to make a request
    /// and wait for its row.
    /// </summary>
    private static async Task<AuditProbe> AuditingProbeAsync(HttpClient adminClient)
    {
        using var marker = await adminClient.GetAsync($"{TestConstants.AuditsBasePath}/summary");
        marker.StatusCode.ShouldBe(HttpStatusCode.OK);

        for (var attempt = 0; attempt < 60; attempt++)
        {
            using var response = await adminClient.GetAsync(
                $"{TestConstants.AuditsBasePath}?pageNumber=1&pageSize=100");

            if (response.StatusCode == HttpStatusCode.OK)
            {
                var page = await response.Content.ReadFromJsonAsync<PagedAudits>(Json);
                var row = page!.Items.FirstOrDefault(a =>
                    !string.IsNullOrEmpty(a.CorrelationId) && !string.IsNullOrEmpty(a.TraceId));

                if (row is not null)
                {
                    return new AuditProbe(row.Id, row.CorrelationId!, row.TraceId!);
                }
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("no audit row with a correlation and trace id materialised for the sweep");
    }

    private sealed record AuditProbe(string Id, string CorrelationId, string TraceId);

    private sealed record IdDto(string Id);

    private sealed record ProfileDto(string Id);

    private sealed record PresignedDto(Guid FileAssetId);

    private sealed record GrantRow(Guid Id, string? ImpersonatedUserId);

    private sealed record PagedIds(List<SessionRow> Items);

    private sealed record SessionRow(string Id, string? UserId);

    private sealed record PagedAudits(List<AuditRow> Items);

    private sealed record AuditRow(string Id, string? CorrelationId, string? TraceId);
}
