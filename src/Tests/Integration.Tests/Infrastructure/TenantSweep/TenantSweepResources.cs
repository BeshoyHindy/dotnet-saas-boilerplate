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

    // Rows seeded in a NON-DEFAULT state. A list endpoint that only shows such rows (the trash
    // view, the sessions list with includeInactive=true) is invisible to the list half of the
    // sweep unless the other tenant actually has one — which is how the trash leak fixed in
    // 2498563 survived the sweep the first time. Each of these is a second row of an existing
    // kind, moved into the state that makes it visible where the default row is not.

    /// <summary>A soft-deleted file: only <c>GET files/trash</c> and the restore route see it.</summary>
    TrashedFile,

    /// <summary>A revoked session: only <c>GET identity/sessions?includeInactive=true</c> shows it.</summary>
    RevokedSession,

    /// <summary>A deactivated user: shown by the user lists, and by <c>isActive=false</c> searches.</summary>
    DeactivatedUser,

    /// <summary>A read notification: shown whenever the inbox is not filtered to unread only.</summary>
    ReadNotification,
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

    /// <summary>
    /// Per-route overrides, for the handful of endpoints that address a row only ever reachable in a
    /// non-default state. <c>POST files/{id}/restore</c> is the example: pointed at a live file it
    /// short-circuits as "already live", so probing it with the ordinary file id asks a weaker
    /// question than the route deserves — the leak it guards is reading another tenant's
    /// <i>trash</i>. Keyed by "<c>METHOD template</c>" and parameter name so a route with two ids
    /// cannot be redirected by accident.
    /// </summary>
    public static IReadOnlyDictionary<(string Endpoint, string Parameter), ResourceKind> ByEndpointParameter { get; } =
        new Dictionary<(string, string), ResourceKind>
        {
            [("POST api/v{version:apiVersion}/files/{id:guid}/restore", "id")] = ResourceKind.TrashedFile,
        };

    /// <summary>The resource one parameter of one route addresses: the override first, then the key.</summary>
    public static ResourceKind KindFor(string endpointName, ResourceParameter parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);

        return ByEndpointParameter.TryGetValue((endpointName, parameter.Name), out var overridden)
            ? overridden
            : ByRouteKey[parameter.RegistryKey];
    }
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
    /// <summary>
    /// The list endpoints that answer a tenant admin with something other than 2xx, and why. Every
    /// other collection on the versioned API must answer 2xx to the sweep's probe, because a list
    /// that refuses is a list the sweep did not search — and one that starts refusing quietly is a
    /// hole that opens without anybody noticing.
    ///
    /// Root-only lists belong here: their permission is flagged <c>IsRoot</c>, a tenant admin can
    /// never hold it, and 403 is the correct answer rather than a leak. Anything else in this
    /// dictionary should be read as a bug someone decided to live with, and say so.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ListsThatRefuseATenantAdmin { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GET api/v{version:apiVersion}/tenants/"] =
                "the tenant catalog: Tenants.View is an IsRoot permission, so 403 is the right answer " +
                "to a tenant admin and the root-token pass is what sweeps this route",
            ["GET api/v{version:apiVersion}/tenants/migrations"] =
                "the migration status of every tenant: Tenants.View is IsRoot, same as the catalog",
            ["GET api/v{version:apiVersion}/tenants/{tenant}/auth/confirm-email"] =
                "not a collection at all — an anonymous confirmation action whose only route parameter " +
                "is the sanctioned {tenant} segment, so the sweep classifies it as a list. It answers " +
                "400 for the missing userId/code, and reads no collection to leak",
        };

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
        ids[ResourceKind.File] = await SeedFileAsync(adminClient, $"sweep-{marker}.pdf");
        // The inbox is per-user and the sweep calls with the ADMIN's token, so the notification has
        // to belong to the admin or the positive control 404s for the right reason and the wrong one.
        var adminUserId = await CurrentUserIdAsync(adminClient);
        ids[ResourceKind.Notification] = await SeedNotificationAsync(
            factory, tenantId, adminUserId, $"sweep-notification-{marker}", marker);
        ids[ResourceKind.ImpersonationGrant] = await SeedImpersonationGrantAsync(adminClient, tenantId, user.UserId);
        ids[ResourceKind.Session] = await SeedSessionAsync(auth, adminClient, user, tenantId);
        ids[ResourceKind.Tenant] = tenantId;

        // Rows in a non-default state. Without these, the list half of the sweep only ever reads the
        // default view of every collection, and a leak confined to the trash view (or to a revoked
        // session, or a deactivated user) is invisible to it because the OTHER tenant has nothing to
        // leak. Each of these carries the tenant marker in its display fields, exactly like the rows
        // above, so no list endpoint needs to know they exist.
        ids[ResourceKind.TrashedFile] = await SeedTrashedFileAsync(adminClient, marker);
        ids[ResourceKind.RevokedSession] = await SeedRevokedSessionAsync(
            auth, adminClient, user, tenantId, ids[ResourceKind.Session]);
        ids[ResourceKind.DeactivatedUser] = await SeedDeactivatedUserAsync(factory, adminClient, tenantId, marker);
        ids[ResourceKind.ReadNotification] = await SeedReadNotificationAsync(
            factory, adminClient, tenantId, adminUserId, marker);

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
    private static async Task<string> SeedFileAsync(HttpClient adminClient, string fileName)
    {
        using var response = await adminClient.PostAsJsonAsync(
            "/api/v1/files/upload-url",
            new
            {
                ownerType = "MyFiles",
                ownerId = (Guid?)null,
                fileName,
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
        AppWebApplicationFactory factory, string tenantId, string userId, string title, string marker)
    {
        var scope = factory.Services.GetRequiredService<ITenantScope>();

        return await scope.RunAsync(tenantId, async (services, ct) =>
        {
            var db = services.GetRequiredService<NotificationsDbContext>();
            var notification = Notification.Create(
                userId,
                "sweep.probe",
                title,
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
    /// A file in the trash: created like any other, then deleted through <c>DELETE files/{id}</c> so
    /// it is soft-deleted exactly the way a user's delete leaves it.
    ///
    /// This is the row that makes the list half of the sweep able to see a trash leak at all. The bug
    /// fixed in 2498563 — a bare <c>IgnoreQueryFilters()</c> in the trash query — stripped the tenant
    /// filter, so tenant A's trash view listed every tenant's deleted files; with no deleted file in
    /// tenant B there was nothing for the marker search to find, and the list test stayed green.
    /// </summary>
    private static async Task<string> SeedTrashedFileAsync(HttpClient adminClient, string marker)
    {
        var fileId = await SeedFileAsync(adminClient, $"sweep-trashed-{marker}.pdf");

        using var deleted = await adminClient.DeleteAsync($"/api/v1/files/{fileId}");
        ((int)deleted.StatusCode).ShouldBeInRange(
            200, 299,
            $"soft-deleting the sweep's trash row failed: {await deleted.Content.ReadAsStringAsync()}");

        return fileId;
    }

    /// <summary>
    /// A revoked session. The tenant sessions list hides revoked rows unless asked for them
    /// (<c>includeInactive=true</c>), so without one seeded here that view is swept against an empty
    /// set — it cannot leak what the other tenant does not have.
    ///
    /// The second login is what mints it; the admin revoke route then takes it out of service. The
    /// user's first session (<paramref name="activeSessionId"/>) is deliberately left alone: the
    /// sweep needs a live session of tenant B to prove the cross-tenant revoke probes did nothing.
    /// </summary>
    private static async Task<string> SeedRevokedSessionAsync(
        AuthHelper auth,
        HttpClient adminClient,
        TenantFixture.TestUser user,
        string tenantId,
        string activeSessionId)
    {
        await TenantFixture.GetTokenWithRetryAsync(auth, user.Email, user.Password, tenantId);

        using var listed = await adminClient.GetAsync(
            $"{TestConstants.IdentityBasePath}/users/{user.UserId}/sessions");
        listed.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"listing the seeded user's sessions failed: {await listed.Content.ReadAsStringAsync()}");

        var sessions = await listed.Content.ReadFromJsonAsync<List<SessionRow>>(Json);
        var doomed = sessions!.FirstOrDefault(s => !string.Equals(s.Id, activeSessionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"the second login did not mint a second session for {user.UserId} in tenant {tenantId}");

        using var revoked = await adminClient.DeleteAsync(
            $"{TestConstants.IdentityBasePath}/users/{user.UserId}/sessions/{doomed.Id}");
        ((int)revoked.StatusCode).ShouldBeInRange(
            200, 299,
            $"revoking the sweep's second session failed: {await revoked.Content.ReadAsStringAsync()}");

        return doomed.Id;
    }

    /// <summary>
    /// A deactivated user, so the user lists and the <c>isActive=false</c> search are swept against a
    /// row that exists in the other tenant rather than against nothing.
    /// </summary>
    private static async Task<string> SeedDeactivatedUserAsync(
        AppWebApplicationFactory factory, HttpClient adminClient, string tenantId, string marker)
    {
        var user = await TenantFixture.RegisterAndConfirmUserAsync(
            factory, adminClient, tenantId, $"sweepoff{marker}");

        using var response = await adminClient.PatchAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/users/{user.UserId}",
            new { userId = user.UserId, activateUser = false });
        ((int)response.StatusCode).ShouldBeInRange(
            200, 299,
            $"deactivating the sweep's user failed: {await response.Content.ReadAsStringAsync()}");

        return user.UserId;
    }

    /// <summary>
    /// A notification already marked read, for the same reason: "read" is a state the inbox shows
    /// only when it is not filtered to unread, and the sweep should have one to look for.
    /// </summary>
    private static async Task<string> SeedReadNotificationAsync(
        AppWebApplicationFactory factory,
        HttpClient adminClient,
        string tenantId,
        string adminUserId,
        string marker)
    {
        var id = await SeedNotificationAsync(
            factory, tenantId, adminUserId, $"sweep-read-notification-{marker}", marker);

        using var response = await adminClient.PostAsync($"/api/v1/notifications/{id}/read", content: null);
        ((int)response.StatusCode).ShouldBeInRange(
            200, 299,
            $"marking the sweep's notification read failed: {await response.Content.ReadAsStringAsync()}");

        return id;
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
