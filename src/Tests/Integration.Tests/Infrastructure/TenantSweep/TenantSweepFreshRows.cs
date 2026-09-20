using System.Net.Http.Json;
using System.Text.Json;

namespace Integration.Tests.Infrastructure.TenantSweep;

/// <summary>
/// Rows minted one at a time for the destructive half of the sweep.
///
/// The positive control for a DELETE has to succeed to be worth anything — and succeeding destroys
/// the row. Reusing the fixture's seeded rows would therefore make the order of the tests part of
/// their meaning. Each destructive control gets its own row instead, created through the same public
/// API, so it can be consumed without consequence.
///
/// <para><b>Two kinds of factory.</b> Most routes name one resource and a per-kind factory is
/// enough. Three do not: removing a member needs a user who <i>is</i> a member of that group,
/// revoking a session needs a session belonging to the <i>caller</i> (the self-service route refuses
/// anyone else's), and the admin revoke needs a session belonging to the user in the path. For those
/// the mint is keyed by route, so the ids it hands back agree with each other. A route with neither
/// must be named in <see cref="DestructiveRoutesWithoutAControl"/> with a reason — silence is not an
/// option, because a route with no control is a route whose 404 for the other tenant proves
/// nothing.</para>
/// </summary>
internal static class TenantSweepFreshRows
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Destructive routes deliberately left without a positive control, and why. An entry here is a
    /// statement that the route's 404 for tenant B is worth less than the others', so keep the list
    /// empty if you can.
    /// </summary>
    public static IReadOnlyDictionary<string, string> DestructiveRoutesWithoutAControl { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
        };

    /// <summary>
    /// Routes whose ids must be minted together. Each returns every id the route needs, or null when
    /// the mint failed — which the caller turns into a failure, not a skip.
    /// </summary>
    private static Dictionary<
        string,
        Func<TenantSweepFixture, SeededTenant, string, Task<Dictionary<ResourceKind, string>?>>> ByEndpoint { get; } =
        new Dictionary<string, Func<TenantSweepFixture, SeededTenant, string, Task<Dictionary<ResourceKind, string>?>>>(
            StringComparer.Ordinal)
        {
            ["DELETE api/v{version:apiVersion}/identity/groups/{groupId:guid}/members/{userId}"] =
                CreateGroupMembershipAsync,
            ["DELETE api/v{version:apiVersion}/identity/sessions/{sessionId:guid}"] =
                CreateCallerSessionAsync,
            ["DELETE api/v{version:apiVersion}/identity/users/{userId:guid}/sessions/{sessionId:guid}"] =
                CreateUserWithASessionAsync,
        };

    /// <summary>
    /// Every id <paramref name="endpointName"/> needs, minted fresh in <paramref name="tenant"/>, or
    /// null when this route has no factory at all.
    /// </summary>
    public static async Task<IReadOnlyDictionary<ResourceKind, string>?> TryCreateAsync(
        TenantSweepFixture sweep,
        SeededTenant tenant,
        string endpointName,
        IReadOnlyCollection<ResourceKind> kinds)
    {
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(tenant);
        ArgumentNullException.ThrowIfNull(kinds);

        var unique = Guid.NewGuid().ToString("N")[..8];

        if (ByEndpoint.TryGetValue(endpointName, out var factory))
        {
            return await factory(sweep, tenant, unique);
        }

        var fresh = new Dictionary<ResourceKind, string>();

        foreach (var kind in kinds)
        {
            var id = await TryCreateOneAsync(sweep, tenant, kind, unique);
            if (id is null)
            {
                return null;
            }

            fresh[kind] = id;
        }

        return fresh;
    }

    private static async Task<string?> TryCreateOneAsync(
        TenantSweepFixture sweep, SeededTenant tenant, ResourceKind kind, string unique) => kind switch
        {
            ResourceKind.Role => await CreateRoleAsync(tenant, unique),
            ResourceKind.Group => await CreateGroupAsync(tenant, unique),
            ResourceKind.File => await CreateFileAsync(tenant, unique),
            ResourceKind.TrashedFile => await CreateTrashedFileAsync(tenant, unique),
            ResourceKind.User => (await CreateUserAsync(sweep, tenant, unique))?.UserId,
            _ => null,
        };

    private static async Task<string?> CreateRoleAsync(SeededTenant tenant, string unique)
    {
        using var response = await tenant.AdminClient.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/roles",
            new { id = string.Empty, name = $"sweep-doomed-{unique}", description = "destructive control" });

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var dto = await response.Content.ReadFromJsonAsync<IdDto>(Json);
        return dto?.Id;
    }

    private static async Task<string?> CreateGroupAsync(SeededTenant tenant, string unique)
    {
        using var response = await tenant.AdminClient.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/groups",
            new { name = $"sweep-doomed-{unique}", description = "destructive control", isDefault = false });

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var dto = await response.Content.ReadFromJsonAsync<IdDto>(Json);
        return dto?.Id;
    }

    private static async Task<string?> CreateFileAsync(SeededTenant tenant, string unique)
    {
        using var response = await tenant.AdminClient.PostAsJsonAsync(
            "/api/v1/files/upload-url",
            new
            {
                ownerType = "MyFiles",
                ownerId = (Guid?)null,
                fileName = $"sweep-doomed-{unique}.pdf",
                contentType = "application/pdf",
                sizeBytes = 256,
                visibility = 1,
                category = "Document",
            });

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var dto = await response.Content.ReadFromJsonAsync<FileDto>(Json);
        return dto?.FileAssetId.ToString();
    }

    /// <summary>A file already in the trash, for the routes that address the trash view.</summary>
    private static async Task<string?> CreateTrashedFileAsync(SeededTenant tenant, string unique)
    {
        var id = await CreateFileAsync(tenant, unique);
        if (id is null)
        {
            return null;
        }

        using var deleted = await tenant.AdminClient.DeleteAsync($"/api/v1/files/{id}");
        return deleted.IsSuccessStatusCode ? id : null;
    }

    /// <summary>
    /// A throwaway user, so <c>DELETE identity/users/{id}</c> has a row to delete that nothing else
    /// in the sweep is holding on to.
    /// </summary>
    private static async Task<TenantFixture.TestUser?> CreateUserAsync(
        TenantSweepFixture sweep, SeededTenant tenant, string unique)
    {
        return await TenantFixture.RegisterAndConfirmUserAsync(
            sweep.Factory, tenant.AdminClient, tenant.TenantId, $"doomed{unique}");
    }

    /// <summary>
    /// A throwaway user who is a member of a throwaway group: the remove-member route 404s for a
    /// user who is not in the group, so two unrelated fresh rows would have made a control that
    /// always failed for a reason that has nothing to do with tenants.
    /// </summary>
    private static async Task<Dictionary<ResourceKind, string>?> CreateGroupMembershipAsync(
        TenantSweepFixture sweep, SeededTenant tenant, string unique)
    {
        var groupId = await CreateGroupAsync(tenant, unique);
        var user = await CreateUserAsync(sweep, tenant, unique);

        if (groupId is null || user is null)
        {
            return null;
        }

        using var added = await tenant.AdminClient.PostAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/groups/{groupId}/members",
            new { userIds = new[] { user.UserId } });

        return added.IsSuccessStatusCode
            ? new Dictionary<ResourceKind, string>
            {
                [ResourceKind.Group] = groupId,
                [ResourceKind.User] = user.UserId,
            }
            : null;
    }

    /// <summary>
    /// A session belonging to the <i>caller</i>. <c>DELETE identity/sessions/{sessionId}</c> is
    /// self-service — <c>SessionService.RevokeSessionAsync</c> throws
    /// <see cref="UnauthorizedAccessException"/> for anyone else's session — so the only row that can
    /// make this control succeed is one of the admin's own. A second login mints one without
    /// disturbing the token the sweep is calling with.
    /// </summary>
    private static async Task<Dictionary<ResourceKind, string>?> CreateCallerSessionAsync(
        TenantSweepFixture sweep, SeededTenant tenant, string unique)
    {
        _ = unique;

        await TenantFixture.GetTokenWithRetryAsync(
            sweep.Auth, tenant.AdminEmail, TestConstants.DefaultPassword, tenant.TenantId);

        var sessionId = await NewestSessionOfAsync(tenant, tenant.AdminUserId);

        return sessionId is null
            ? null
            : new Dictionary<ResourceKind, string> { [ResourceKind.Session] = sessionId };
    }

    /// <summary>
    /// A throwaway user and a session of theirs, for the admin revoke route — which checks that the
    /// session in the path really belongs to the user in the path before it does anything.
    /// </summary>
    private static async Task<Dictionary<ResourceKind, string>?> CreateUserWithASessionAsync(
        TenantSweepFixture sweep, SeededTenant tenant, string unique)
    {
        var user = await CreateUserAsync(sweep, tenant, unique);
        if (user is null)
        {
            return null;
        }

        await TenantFixture.GetTokenWithRetryAsync(sweep.Auth, user.Email, user.Password, tenant.TenantId);

        var sessionId = await NewestSessionOfAsync(tenant, user.UserId);

        return sessionId is null
            ? null
            : new Dictionary<ResourceKind, string>
            {
                [ResourceKind.User] = user.UserId,
                [ResourceKind.Session] = sessionId,
            };
    }

    /// <summary>The most recently created session of a user, read through the admin sessions view.</summary>
    private static async Task<string?> NewestSessionOfAsync(SeededTenant tenant, string userId)
    {
        using var response = await tenant.AdminClient.GetAsync(
            $"{TestConstants.IdentityBasePath}/users/{userId}/sessions");

        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var sessions = await response.Content.ReadFromJsonAsync<List<SessionRow>>(Json);
        return sessions?.OrderByDescending(s => s.CreatedAt).FirstOrDefault()?.Id;
    }

    private sealed record IdDto(string Id);

    private sealed record FileDto(Guid FileAssetId);

    private sealed record SessionRow(string Id, DateTime CreatedAt);
}
