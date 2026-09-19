using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using Integration.Tests.Tests.Sessions;
using System.Text.Json;

namespace Integration.Tests.Tests.Storage;

/// <summary>
/// #83, on real MinIO: <b>the server persists only asset URLs it issued, for the owner it issued
/// them to.</b>
///
/// <para>The tenant boundary (#78) answers "may this tenant touch this object" and inside one
/// tenant it answers nothing. The hole this pins shut lived entirely inside one tenant: user X set
/// their <c>ImageUrl</c> to user Y's avatar URL — any string was accepted — and then replaced or
/// removed their avatar, at which point the delete path found a key the tenant legitimately owns and
/// removed Y's bytes. A tenant admin could do the same to any avatar through a theme asset URL.</para>
///
/// <para>Two changes, and this file asserts the outcome of both rather than their mechanics: there
/// is no longer any way to <i>name</i> an asset URL (the "set my avatar URL" endpoint is gone and
/// the theme's write model has no URL fields), and every delete is scoped to the owner segment the
/// upload wrote (<c>…/appuser/{userId}/…</c>, <c>…/tenanttheme/logo/…</c>), so even a row that still
/// holds someone else's URL cannot be turned into a delete. The assertion that matters is always the
/// same one: Y's object still fetches anonymously.</para>
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class ServerIssuedAssetUrlTests : IAsyncLifetime
{
    private const string ThemePath = $"{TestConstants.TenantsBasePath}/theme";
    private const string ProfilePath = $"{TestConstants.IdentityBasePath}/profile";

    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;
    private readonly TenantFixtures _tenants;

    private string _tenantId = default!;
    private string _adminEmail = default!;

    public ServerIssuedAssetUrlTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
        _tenants = new TenantFixtures(factory);
    }

    public async Task InitializeAsync() =>
        (_tenantId, _adminEmail) = await _tenants.CreateProvisionedTenantAsync("owned");

    public Task DisposeAsync() => Task.CompletedTask;

    #region One tenant, two users

    [Fact]
    public async Task UserX_Should_NotBeAbleToCauseTheDeletion_Of_UserYs_Avatar()
    {
        // Arrange — Y uploads an avatar the normal way; it resolves anonymously, which is what an
        // <img src> in the console actually does with it.
        using var adminClient = await AdminClientAsync();
        var (x, y) = (await SeedUserAsync(adminClient, "own-x"), await SeedUserAsync(adminClient, "own-y"));
        using var clientY = await ClientForAsync(y);
        using var clientX = await ClientForAsync(x);

        var avatarY = await UploadAvatarAsync(clientY, "y-avatar.png");
        (await FetchAnonymouslyAsync(avatarY)).ShouldBe(HttpStatusCode.OK);

        // Act — everything X's API allows, with Y's URL in hand.

        // 1. The endpoint that used to take a URL is gone, not merely validated: nothing is mapped
        //    at that route any more.
        using var byUrl = await clientX.PutAsJsonAsync($"{ProfilePath}/image", new { imageUrl = avatarY });
        byUrl.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // 2. Smuggling the URL into the profile PUT: the write model has no such field, so it is
        //    ignored by the deserializer and X's column stays empty.
        using var smuggled = await clientX.PutAsJsonAsync(ProfilePath, new
        {
            firstName = "Mallory",
            imageUrl = avatarY,
            image = (object?)null,
        });
        smuggled.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ProfileImageUrlAsync(clientX)).ShouldBeNull("no request field can write the avatar column");

        // 3. X then does the whole avatar lifecycle — upload, replace, remove — which is where the
        //    deletes happen. None of them may reach Y's object.
        await UploadAvatarAsync(clientX, "x-one.png");
        await UploadAvatarAsync(clientX, "x-two.png");
        await RemoveAvatarAsync(clientX);

        // Assert — Y's bytes and Y's column are exactly as they were.
        (await FetchAnonymouslyAsync(avatarY)).ShouldBe(HttpStatusCode.OK, "user Y's avatar must survive everything X can do");
        (await ProfileImageUrlAsync(clientY)).ShouldBe(avatarY);
    }

    [Fact]
    public async Task AnAvatarUrl_Should_CarryTheOwningUserId_SoADeleteCanBeScopedToIt()
    {
        using var adminClient = await AdminClientAsync();
        var user = await SeedUserAsync(adminClient, "own-key");
        using var client = await ClientForAsync(user);

        var avatar = await UploadAvatarAsync(client, "keyed.png");

        // The owner segment is the block's, composed from the user id — the caller never writes a
        // key (ADR-0002, and Architecture.Tests/StorageKeyOwnershipTests scans for it).
        // Case-insensitively: the block lower-cases the owner segment, ASP.NET Identity does not
        // promise the id's casing, and which of the two wins is not what this test is about.
        avatar.ShouldContain($"/uploads/tenants/{_tenantId}/appuser/{user.UserId}/", Case.Insensitive);
    }

    [Fact]
    public async Task ReplacingAnAvatar_Should_DeleteTheCallersOwnPreviousObject()
    {
        using var adminClient = await AdminClientAsync();
        var user = await SeedUserAsync(adminClient, "own-rep");
        using var client = await ClientForAsync(user);

        var first = await UploadAvatarAsync(client, "first.png");
        var second = await UploadAvatarAsync(client, "second.png");

        second.ShouldNotBe(first);
        (await FetchAnonymouslyAsync(first)).ShouldNotBe(HttpStatusCode.OK, "the replaced object is nobody's to keep");
        (await FetchAnonymouslyAsync(second)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task RemovingAnAvatar_Should_ClearTheColumn_AndDeleteTheObject()
    {
        using var adminClient = await AdminClientAsync();
        var user = await SeedUserAsync(adminClient, "own-del");
        using var client = await ClientForAsync(user);
        var avatar = await UploadAvatarAsync(client, "gone.png");

        await RemoveAvatarAsync(client);

        (await ProfileImageUrlAsync(client)).ShouldBeNull();
        (await FetchAnonymouslyAsync(avatar)).ShouldNotBe(HttpStatusCode.OK);
    }

    #endregion

    #region Brand assets, under a tenant admin

    [Fact]
    public async Task TenantAdmin_Should_NotBeAbleToPointABrandAsset_AtAUsersAvatar()
    {
        // The admin holds Tenants.UpdateTheme and every avatar in the tenant is a key the tenant
        // owns, so before #83 parking one in LogoUrl and saving twice deleted it.
        using var adminClient = await AdminClientAsync();
        var victim = await SeedUserAsync(adminClient, "own-victim");
        using var victimClient = await ClientForAsync(victim);
        var avatar = await UploadAvatarAsync(victimClient, "victim.png");

        // Act — the URL fields are not on the write model, so this is accepted and ignored.
        using var pointed = await adminClient.PutAsJsonAsync(ThemePath, new
        {
            brandAssets = new { logoUrl = avatar, logoDarkUrl = avatar, faviconUrl = avatar },
        });
        pointed.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await BrandAssetUrlAsync(adminClient, "logoUrl")).ShouldBeNull("a client-supplied URL must never reach the column");

        // …and the deletes that follow a real logo's lifecycle find nothing of the victim's.
        await UploadBrandAssetAsync(adminClient, "logo", "real-logo.png");
        await UploadBrandAssetAsync(adminClient, "logo", "real-logo-2.png");
        await DeleteBrandAssetAsync(adminClient, "deleteLogo");

        (await FetchAnonymouslyAsync(avatar)).ShouldBe(HttpStatusCode.OK);
        (await ProfileImageUrlAsync(victimClient)).ShouldBe(avatar);
    }

    [Fact]
    public async Task ReplacingABrandAsset_Should_DeleteOnlyThatSlotsPreviousObject()
    {
        using var adminClient = await AdminClientAsync();

        var favicon = await UploadBrandAssetAsync(adminClient, "favicon", "fav.png");
        var logo = await UploadBrandAssetAsync(adminClient, "logo", "logo-one.png");
        var replacement = await UploadBrandAssetAsync(adminClient, "logo", "logo-two.png");

        replacement.ShouldNotBe(logo);
        logo.ShouldContain($"/uploads/tenants/{_tenantId}/tenanttheme/logo/");
        (await FetchAnonymouslyAsync(logo)).ShouldNotBe(HttpStatusCode.OK);
        (await FetchAnonymouslyAsync(replacement)).ShouldBe(HttpStatusCode.OK);
        (await FetchAnonymouslyAsync(favicon)).ShouldBe(HttpStatusCode.OK, "another slot's asset is not the logo's to delete");
    }

    [Fact]
    public async Task RemovingABrandAsset_Should_ClearTheColumn_AndDeleteTheObject()
    {
        using var adminClient = await AdminClientAsync();
        var logo = await UploadBrandAssetAsync(adminClient, "logo", "bye.png");

        await DeleteBrandAssetAsync(adminClient, "deleteLogo");

        (await BrandAssetUrlAsync(adminClient, "logoUrl")).ShouldBeNull();
        (await FetchAnonymouslyAsync(logo)).ShouldNotBe(HttpStatusCode.OK);
    }

    /// <summary>
    /// The bytes now ride on the theme save, so the extension check has to happen in the validator.
    /// It used to happen inside <c>IStorageService.UploadAsync</c>, which throws
    /// <c>InvalidOperationException</c> — a 500 for a caller's mistake, after the request had already
    /// been let through. The save is rejected whole: the column keeps the logo it had and that
    /// object is still fetchable.
    /// </summary>
    [Fact]
    public async Task ARejectedBrandAsset_Should_Answer400_AndLeaveTheStoredLogoAlone()
    {
        using var adminClient = await AdminClientAsync();
        var logo = await UploadBrandAssetAsync(adminClient, "logo", "keeper.png");

        using var response = await adminClient.PutAsJsonAsync(ThemePath, new
        {
            brandAssets = new
            {
                logo = new { fileName = "payload.svg", contentType = "image/svg+xml", data = PngBytes },
            },
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).ShouldContain(".png", Case.Insensitive, "the ProblemDetails names the allow-list");

        (await BrandAssetUrlAsync(adminClient, "logoUrl")).ShouldBe(logo, "a rejected save writes nothing");
        (await FetchAnonymouslyAsync(logo)).ShouldBe(HttpStatusCode.OK, "and deletes nothing");
    }

    #endregion

    #region Helpers

    private Task<HttpClient> AdminClientAsync() =>
        _auth.CreateAuthenticatedClientAsync(_adminEmail, TestConstants.DefaultPassword, _tenantId);

    private Task<SeededUser> SeedUserAsync(HttpClient adminClient, string prefix) =>
        IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, prefix, _tenantId);

    private Task<HttpClient> ClientForAsync(SeededUser user) =>
        _auth.CreateAuthenticatedClientAsync(user.Email, user.Password, _tenantId);

    /// <summary>The one way an avatar is set: bytes on the profile PUT, URL issued by the server.</summary>
    private static async Task<string> UploadAvatarAsync(HttpClient client, string fileName)
    {
        using var response = await client.PutAsJsonAsync(ProfilePath, new
        {
            firstName = "Ada",
            lastName = "Lovelace",
            image = new
            {
                fileName,
                contentType = "image/png",
                data = PngBytes,
            },
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());

        var url = await ProfileImageUrlAsync(client);
        url.ShouldNotBeNullOrWhiteSpace();
        return url!;
    }

    private static async Task RemoveAvatarAsync(HttpClient client)
    {
        using var response = await client.PutAsJsonAsync(ProfilePath, new
        {
            firstName = "Ada",
            lastName = "Lovelace",
            deleteCurrentImage = true,
        });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string?> ProfileImageUrlAsync(HttpClient client)
    {
        using var response = await client.GetAsync(ProfilePath);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return (await response.DeserializeAsync<UserDto>()).ImageUrl;
    }

    /// <summary>
    /// Uploads one brand-asset slot (<c>logo</c>, <c>logoDark</c>, <c>favicon</c>) and returns the
    /// URL the server issued for it. Everything the theme's write model does not mention keeps its
    /// default, which is all these tests need.
    /// </summary>
    private static async Task<string> UploadBrandAssetAsync(HttpClient client, string slot, string fileName)
    {
        var assets = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [slot] = new { fileName, contentType = "image/png", data = PngBytes },
        };

        using var response = await client.PutAsJsonAsync(ThemePath, new { brandAssets = assets });
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());

        var url = await BrandAssetUrlAsync(client, $"{slot}Url");
        url.ShouldNotBeNullOrWhiteSpace();
        return url!;
    }

    private static async Task DeleteBrandAssetAsync(HttpClient client, string deleteFlag)
    {
        var assets = new Dictionary<string, object>(StringComparer.Ordinal) { [deleteFlag] = true };

        using var response = await client.PutAsJsonAsync(ThemePath, new { brandAssets = assets });
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, await response.Content.ReadAsStringAsync());
    }

    private static async Task<string?> BrandAssetUrlAsync(HttpClient client, string property)
    {
        using var response = await client.GetAsync(ThemePath);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("brandAssets").GetProperty(property).GetString();
    }

    /// <summary>
    /// The durable public URL is meant to be readable with no credential at all — that is what the
    /// deploy stacks' anonymous grant on <c>uploads/</c> is for — so "is the object still there" is
    /// asked the way a browser asks it.
    /// </summary>
    private static async Task<HttpStatusCode> FetchAnonymouslyAsync(string url)
    {
        using var anonymous = new HttpClient();
        using var response = await anonymous.GetAsync(new Uri(url));
        return response.StatusCode;
    }

    /// <summary>A PNG magic number — `data` is a JSON array of numbers on the wire (List&lt;byte&gt;).</summary>
    private static int[] PngBytes => [137, 80, 78, 71, 13, 10, 26, 10];

    #endregion
}
