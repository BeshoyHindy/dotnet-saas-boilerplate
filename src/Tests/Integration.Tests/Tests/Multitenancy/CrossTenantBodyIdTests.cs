using System.Net.Http.Json;
using System.Text.Json;
using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.TenantSweep;

namespace Integration.Tests.Tests.Multitenancy;

/// <summary>
/// The sweep's structural blind spot, covered by hand.
///
/// <see cref="TenantEndpointSweepTests"/> enumerates <c>EndpointDataSource</c> and substitutes the
/// other tenant's id into every <b>route</b> parameter. An id that arrives in the <b>body</b> — a
/// role id inside an upsert, a list of user ids inside an add-members call, an owner id inside an
/// upload request — is invisible to it: there is no route parameter to substitute, so the endpoint
/// is swept as if it addressed nothing at all. Those ids reach the same handlers and the same
/// queries, and they are the ones a client can set freely.
///
/// Each case here sends tenant A's token with tenant B's id in the payload and asserts both halves:
/// nothing of B changed, and nothing of B was granted to A. The expected status differs per
/// endpoint, because what the handler does with the id differs — which is the point of writing them
/// out rather than asserting one blanket rule.
///
/// Shares <see cref="TenantSweepFixture"/> with the sweep: the same two provisioned tenants, the
/// same seeded rows, no second Testcontainers host.
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class CrossTenantBodyIdTests
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppWebApplicationFactory _factory;

    public CrossTenantBodyIdTests(AppWebApplicationFactory factory) => _factory = factory;

    private Task<TenantSweepFixture> SweepAsync() => TenantSweepFixture.GetAsync(_factory);

    /// <summary>
    /// <c>POST identity/roles</c> is an upsert: a non-empty id means "update that role". Handed
    /// tenant B's role id it finds nothing — <c>RoleManager.FindByIdAsync</c> runs inside the tenant
    /// filter — and falls through to the create branch.
    ///
    /// That is acceptable and is asserted as such: the new role is tenant A's own, with an id the
    /// server minted, so no foreign id is persisted and the answer carries no information about
    /// whether that id exists anywhere. It is the same answer the caller would get for an id nobody
    /// has ever issued, which is exactly the property ADR-0002 asks for. What would <i>not</i> be
    /// acceptable — B's role renamed, or A's role adopting B's id — is what this test pins.
    /// </summary>
    [Fact]
    public async Task Upserting_A_Role_With_Another_Tenants_Role_Id_Creates_A_Local_Role_And_Leaves_Theirs_Alone()
    {
        var sweep = await SweepAsync();
        var before = await ReadRoleAsync(sweep.B, sweep.B[ResourceKind.Role]);

        using var response = await sweep.A.AdminClient.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/roles",
            new
            {
                id = sweep.B[ResourceKind.Role],
                name = $"hijack-{sweep.A.Marker}",
                description = "cross-tenant body id probe",
            });

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"the upsert should have created a role in tenant A: {await response.Content.ReadAsStringAsync()}");

        var created = await response.Content.ReadFromJsonAsync<RoleDto>(Json);
        created!.Id.ShouldNotBe(
            sweep.B[ResourceKind.Role],
            "tenant A must not end up holding a row whose id belongs to tenant B");
        created.Name.ShouldBe($"hijack-{sweep.A.Marker}");

        var after = await ReadRoleAsync(sweep.B, sweep.B[ResourceKind.Role]);
        after.Name.ShouldBe(before.Name, "tenant B's role must not have been renamed by tenant A");
        after.Description.ShouldBe(before.Description);

        using var stillInvisible = await sweep.A.AdminClient.GetAsync(
            $"{TestConstants.IdentityBasePath}/roles/{sweep.B[ResourceKind.Role]}");
        stillInvisible.StatusCode.ShouldBe(
            HttpStatusCode.NotFound,
            "and the upsert must not have made B's role readable to A");
    }

    /// <summary>
    /// <c>POST identity/users/{id}/roles</c> takes the roles in the body. The user is pinned by the
    /// route (and so by the sweep), but the roles are not: the payload names them.
    ///
    /// The assignment resolves roles by <i>name</i> inside the tenant filter, so B's role name finds
    /// nothing in A and the entry is skipped. The call still answers 200 — it is a bulk "set these
    /// flags" operation and an unknown role is a no-op — so what has to be proven is the effect:
    /// A's user gains nothing, and B's user's roles are untouched.
    /// </summary>
    [Fact]
    public async Task Assigning_Another_Tenants_Role_To_A_Local_User_Grants_Nothing()
    {
        var sweep = await SweepAsync();
        var foreignRole = await ReadRoleAsync(sweep.B, sweep.B[ResourceKind.Role]);
        var subject = sweep.A[ResourceKind.User];

        using var response = await sweep.A.AdminClient.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/users/{subject}/roles",
            new
            {
                userId = subject,
                userRoles = new[]
                {
                    new
                    {
                        roleId = sweep.B[ResourceKind.Role],
                        roleName = foreignRole.Name,
                        enabled = true,
                    },
                },
            });

        ((int)response.StatusCode).ShouldBeLessThan(
            500,
            $"the assignment must not blow up: {await response.Content.ReadAsStringAsync()}");

        var roles = await ReadUserRolesAsync(sweep.A, subject);
        roles.ShouldNotContain(
            r => string.Equals(r.RoleId, sweep.B[ResourceKind.Role], StringComparison.OrdinalIgnoreCase),
            "tenant B's role id must not appear anywhere in a tenant-A user's role list");
        roles.ShouldNotContain(
            r => r.Enabled && string.Equals(r.RoleName, foreignRole.Name, StringComparison.OrdinalIgnoreCase),
            "and no role of that name may have been switched on for them");

        var subjectOfB = await ReadUserRolesAsync(sweep.B, sweep.B[ResourceKind.User]);
        subjectOfB.ShouldNotContain(
            r => r.Enabled && string.Equals(r.RoleName, foreignRole.Name, StringComparison.OrdinalIgnoreCase),
            "and tenant B's own user must not have been enrolled as a side effect");
    }

    /// <summary>
    /// <c>POST identity/groups/{groupId}/members</c> takes the users in the body. The handler
    /// validates them against the tenant-filtered user table, so another tenant's user ids are
    /// "not found" — 404, the same answer the sweep demands of a route id.
    /// </summary>
    [Fact]
    public async Task Adding_Another_Tenants_User_To_A_Local_Group_Is_Not_Found()
    {
        var sweep = await SweepAsync();
        var group = sweep.A[ResourceKind.Group];

        using var response = await sweep.A.AdminClient.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/groups/{group}/members",
            new { userIds = new[] { sweep.B[ResourceKind.User] } });

        response.StatusCode.ShouldBe(
            HttpStatusCode.NotFound,
            "a user of another tenant does not exist as far as this tenant is concerned: " +
            await response.Content.ReadAsStringAsync());

        using var members = await sweep.A.AdminClient.GetAsync(
            $"{TestConstants.IdentityBasePath}/groups/{group}/members");
        members.StatusCode.ShouldBe(HttpStatusCode.OK);

        var body = await members.Content.ReadAsStringAsync();
        body.ShouldNotContain(sweep.B[ResourceKind.User], Case.Insensitive);
        body.ShouldNotContain(sweep.B.Marker, Case.Insensitive);
    }

    /// <summary>
    /// <c>POST files/upload-url</c> takes the owner the file is being attached to in the body. The
    /// built-in policy for the <c>MyFiles</c>/<c>User</c> owner types is uploader-only on read and on
    /// delete, so an upload attached to somebody else's owner id was a row nobody could ever read —
    /// and, with another tenant's user id in it, a row in tenant A carrying a reference to a subject
    /// of tenant B, minted by a caller who cannot see that subject exists.
    ///
    /// The policy now refuses it. The refusal is a flat 403 for <i>any</i> owner that is not the
    /// caller, so it says nothing about whether the id exists — in this tenant or another.
    /// </summary>
    [Fact]
    public async Task Requesting_An_Upload_Owned_By_Another_Tenants_User_Is_Forbidden()
    {
        var sweep = await SweepAsync();

        using var response = await sweep.A.AdminClient.PostAsJsonAsync(
            "/api/v1/files/upload-url",
            new
            {
                ownerType = "User",
                ownerId = sweep.B[ResourceKind.User],
                fileName = $"hijack-{sweep.A.Marker}.pdf",
                contentType = "application/pdf",
                sizeBytes = 256,
                visibility = 1,
                category = "Document",
            });

        response.StatusCode.ShouldBe(
            HttpStatusCode.Forbidden,
            $"an upload may only be attached to the caller's own owner: {await response.Content.ReadAsStringAsync()}");

        // Nothing was minted: neither tenant's file list knows about the attempt.
        using var mine = await sweep.A.AdminClient.GetAsync("/api/v1/files/mine?pageNumber=1&pageSize=100");
        mine.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await mine.Content.ReadAsStringAsync()).ShouldNotContain($"hijack-{sweep.A.Marker}", Case.Insensitive);

        using var theirs = await sweep.B.AdminClient.GetAsync("/api/v1/files/mine?pageNumber=1&pageSize=100");
        theirs.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await theirs.Content.ReadAsStringAsync()).ShouldNotContain($"hijack-{sweep.A.Marker}", Case.Insensitive);
    }

    /// <summary>
    /// The same request with the caller's <i>own</i> user id as the owner still works — without this
    /// the test above would pass just as well if uploads were broken for everyone.
    /// </summary>
    [Fact]
    public async Task Requesting_An_Upload_Owned_By_The_Caller_Still_Works()
    {
        var sweep = await SweepAsync();

        using var response = await sweep.A.AdminClient.PostAsJsonAsync(
            "/api/v1/files/upload-url",
            new
            {
                ownerType = "User",
                ownerId = sweep.A.AdminUserId,
                fileName = $"own-owner-{sweep.A.Marker}.pdf",
                contentType = "application/pdf",
                sizeBytes = 256,
                visibility = 1,
                category = "Document",
            });

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"attaching a file to your own user is the shipped client's own flow: " +
            await response.Content.ReadAsStringAsync());
    }

    private static async Task<RoleDto> ReadRoleAsync(SeededTenant tenant, string roleId)
    {
        using var response = await tenant.AdminClient.GetAsync(
            $"{TestConstants.IdentityBasePath}/roles/{roleId}");
        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"reading role {roleId} of tenant {tenant.TenantId} failed: " +
            await response.Content.ReadAsStringAsync());

        return (await response.Content.ReadFromJsonAsync<RoleDto>(Json))!;
    }

    private static async Task<List<UserRoleRow>> ReadUserRolesAsync(SeededTenant tenant, string userId)
    {
        using var response = await tenant.AdminClient.GetAsync(
            $"{TestConstants.IdentityBasePath}/users/{userId}/roles");
        response.StatusCode.ShouldBe(
            HttpStatusCode.OK,
            $"reading the roles of user {userId} failed: {await response.Content.ReadAsStringAsync()}");

        return (await response.Content.ReadFromJsonAsync<List<UserRoleRow>>(Json))!;
    }

    private sealed record RoleDto(string Id, string Name, string? Description);

    private sealed record UserRoleRow(string? RoleId, string? RoleName, bool Enabled);
}
