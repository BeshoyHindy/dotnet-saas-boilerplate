using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using Integration.Tests.Tests.Sessions;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Integration.Tests.Tests.Users;

/// <summary>
/// Covers the self-service profile surface: UpdateUser (PUT /profile), which forces the target id to
/// the authenticated user, so any signed-in user may edit their own profile — and is now the only
/// way an avatar is set or cleared (#83; the owner-scoped deletes it performs are asserted end to
/// end in <c>Tests/Storage/ServerIssuedAssetUrlTests</c>).
/// </summary>
[Collection(AppCollectionDefinition.Name)]
public sealed class UserProfileTests
{
    private readonly AppWebApplicationFactory _factory;
    private readonly AuthHelper _auth;

    public UserProfileTests(AppWebApplicationFactory factory)
    {
        _factory = factory;
        _auth = new AuthHelper(factory);
    }

    #region UpdateUser (PUT /profile)

    [Fact]
    public async Task UpdateProfile_Should_PersistChanges_When_AuthenticatedUserUpdatesOwnProfile()
    {
        // Arrange
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "upd-profile");
        using var userClient = await _auth.CreateAuthenticatedClientAsync(user.Email, user.Password);

        // Act
        var response = await userClient.PutAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/profile", new
            {
                firstName = "Updated",
                lastName = "Name",
                phoneNumber = "1234567890"
            });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var profile = await userClient.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        var dto = await profile.DeserializeAsync<UserDto>();
        dto.FirstName.ShouldBe("Updated");
        dto.LastName.ShouldBe("Name");
        dto.PhoneNumber.ShouldBe("1234567890");
    }

    [Fact]
    public async Task UpdateProfile_Should_IgnoreSuppliedId_When_DifferentFromAuthenticatedUser()
    {
        // Arrange — the endpoint forces request.Id to the caller, so supplying another
        // user's id must NOT update that other user.
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var caller = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "upd-self");
        var victim = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "upd-victim");
        using var callerClient = await _auth.CreateAuthenticatedClientAsync(caller.Email, caller.Password);

        // Act — caller tries to update the victim by passing victim's id in the body.
        var response = await callerClient.PutAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/profile", new
            {
                id = victim.UserId,
                firstName = "Hijacked"
            });

        // Assert — request succeeds but only the caller's own profile is touched.
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var victimRecord = await adminClient.GetAsync(
            $"{TestConstants.IdentityBasePath}/users/{victim.UserId}");
        var victimDto = await victimRecord.DeserializeAsync<UserDto>();
        victimDto.FirstName.ShouldNotBe("Hijacked");
    }

    [Fact]
    public async Task UpdateProfile_Should_Return401_When_NotAuthenticated()
    {
        // Arrange
        using var client = _factory.CreateClient();

        // Act
        var response = await client.PutAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/profile", new { firstName = "Nope" });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UpdateProfile_Should_Return400_When_PhoneNumberExceedsMaxLength()
    {
        // Arrange
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "upd-invalid");
        using var userClient = await _auth.CreateAuthenticatedClientAsync(user.Email, user.Password);

        // Act — phone number max length is 15.
        var response = await userClient.PutAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/profile", new
            {
                phoneNumber = new string('9', 30)
            });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateProfile_Should_StoreADurableUploadsUrl_When_AnAvatarIsUploaded()
    {
        // Arrange — this is the path the console's avatar picker takes (issue #72): the image
        // rides on the profile PUT and the server writes it with IStorageService.UploadAsync.
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "avatar-upload");
        using var userClient = await _auth.CreateAuthenticatedClientAsync(user.Email, user.Password);

        // Act — `data` must serialize as a JSON array of numbers (List<byte> on the wire), not base64.
        var response = await userClient.PutAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/profile", new
            {
                firstName = "Ada",
                lastName = "Lovelace",
                image = new
                {
                    fileName = "avatar.png",
                    contentType = "image/png",
                    data = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.Select(b => (int)b).ToArray(),
                },
            });

        // Assert
        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var profile = await userClient.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        var dto = await profile.DeserializeAsync<UserDto>();
        dto.ImageUrl.ShouldNotBeNullOrWhiteSpace();

        // …under the uploads/ prefix, the only key space the deploy stacks grant anonymous read on
        // (contract-tested in deploy/dokploy/tests) — so the avatar keeps resolving.
        dto.ImageUrl!.ShouldContain("/uploads/");

        // …and unsigned: no presign to expire. A Files-module publicUrl would carry these and die
        // within minutes of being written to the column.
        dto.ImageUrl.ShouldNotContain("X-Amz-Signature", Case.Insensitive);
        dto.ImageUrl.ShouldNotContain("X-Amz-Expires", Case.Insensitive);

        // …and stable: re-reading the profile after the presign TTL would have elapsed hands back
        // the identical URL, because nothing about it is minted per-read.
        var again = await userClient.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        var dtoAgain = await again.DeserializeAsync<UserDto>();
        dtoAgain.ImageUrl.ShouldBe(dto.ImageUrl);
    }

    #endregion

    #region ETag / If-Match (#107)

    private static readonly object TextOnlyUpdate = new { firstName = "Grace", lastName = "Hopper" };

    private static HttpRequestMessage PutProfile(object body, EntityTagHeaderValue? ifMatch)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"{TestConstants.IdentityBasePath}/profile")
        {
            Content = JsonContent.Create(body),
        };
        if (ifMatch is not null)
        {
            request.Headers.IfMatch.Add(ifMatch);
        }

        return request;
    }

    private static async Task<EntityTagHeaderValue> CurrentETagAsync(HttpClient client)
    {
        using var response = await client.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return response.Headers.ETag.ShouldNotBeNull();
    }

    [Fact]
    public async Task GetProfile_Should_ReturnTheVersionAsAStrongETag()
    {
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "etag-get");
        using var userClient = await _auth.CreateAuthenticatedClientAsync(user.Email, user.Password);

        var etag = await CurrentETagAsync(userClient);

        etag.IsWeak.ShouldBeFalse();
        etag.Tag.ShouldNotBe("\"\"");
    }

    [Fact]
    public async Task UpdateProfile_Should_Return200_AndMoveTheVersion_When_IfMatchNamesTheCurrentVersion()
    {
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "etag-match");
        using var userClient = await _auth.CreateAuthenticatedClientAsync(user.Email, user.Password);
        var before = await CurrentETagAsync(userClient);

        using var response = await userClient.SendAsync(PutProfile(TextOnlyUpdate, before));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await CurrentETagAsync(userClient)).ShouldNotBe(before);
    }

    [Fact]
    public async Task UpdateProfile_Should_Return200_When_NoIfMatchIsSent()
    {
        // The header is optional on the API; only the clients always send it.
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "etag-none");
        using var userClient = await _auth.CreateAuthenticatedClientAsync(user.Email, user.Password);

        using var response = await userClient.SendAsync(PutProfile(TextOnlyUpdate, ifMatch: null));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateProfile_Should_Return412_WithNoStorageSideEffect_When_IfMatchIsStale()
    {
        // Two editors read the same version; the first saves, so the second's tag is stale.
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "etag-stale");
        using var userClient = await _auth.CreateAuthenticatedClientAsync(user.Email, user.Password);
        var readByBoth = await CurrentETagAsync(userClient);

        using (var first = await userClient.SendAsync(PutProfile(TextOnlyUpdate, readByBoth)))
        {
            first.StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Act — the second editor's save carries an avatar, so a check that ran after the upload
        // would leave an object (and a column value) behind.
        using var stale = await userClient.SendAsync(PutProfile(
            new
            {
                firstName = "Stale",
                lastName = "Write",
                image = AvatarPng(),
            },
            readByBoth));

        // Assert — 412 ProblemDetails, not the 500 a concurrency failure used to be.
        stale.StatusCode.ShouldBe(HttpStatusCode.PreconditionFailed);
        var problem = await stale.Content.ReadFromJsonAsync<JsonElement>();
        problem.GetProperty("status").GetInt32().ShouldBe(412);
        problem.GetProperty("detail").GetString().ShouldNotBeNullOrWhiteSpace();

        // …and nothing of the stale request landed: not the names, and not the avatar.
        using var after = await userClient.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        var dto = await after.DeserializeAsync<UserDto>();
        dto.FirstName.ShouldBe("Grace");
        dto.ImageUrl.ShouldBeNull("the avatar upload happens after the check, so a stale request never stores one");

        var avatarPrefix = $"uploads/tenants/{TestConstants.RootTenantId}/appuser/{user.UserId}/";
        (await StorageObjectsAsync(avatarPrefix)).ShouldBeEmpty("no object may be written for a refused update");

        // Positive control: the same avatar sent with the current version is stored under that
        // prefix, so the empty listing above is evidence rather than a wrong path.
        using var fresh = await userClient.SendAsync(PutProfile(
            new { image = AvatarPng() },
            await CurrentETagAsync(userClient)));
        fresh.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await StorageObjectsAsync(avatarPrefix)).ShouldNotBeEmpty();
    }

    private static object AvatarPng() => new
    {
        fileName = "avatar.png",
        contentType = "image/png",
        data = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.Select(b => (int)b).ToArray(),
    };

    private async Task<IReadOnlyList<string>> StorageObjectsAsync(string prefix)
    {
        using var scope = _factory.Services.CreateScope();
        var s3 = scope.ServiceProvider.GetRequiredService<Amazon.S3.IAmazonS3>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<Boilerplate.BuildingBlocks.Storage.S3.S3StorageOptions>>().Value;
        var listing = await s3.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request
        {
            BucketName = options.Bucket,
            Prefix = prefix,
        });
        return (listing.S3Objects ?? []).Select(o => o.Key).ToList();
    }

    #endregion

    #region The avatar column takes no URL from anyone (#83)

    [Fact]
    public async Task SetProfileImageByUrl_Should_NoLongerExist()
    {
        // PUT /profile/image took any string up to 2048 characters and wrote it to AppUser.ImageUrl.
        // It is gone rather than validated: an avatar is uploaded on the profile PUT and removed with
        // its delete flag, which is the whole surface. 404 — nothing is mapped here.
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "img-gone");
        using var userClient = await _auth.CreateAuthenticatedClientAsync(user.Email, user.Password);

        var response = await userClient.PutAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/profile/image",
            new { imageUrl = "https://cdn.example.com/avatars/me.png" });

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateProfile_Should_IgnoreAnImageUrlField_When_TheBodyCarriesOne()
    {
        // Ignored, not rejected: `imageUrl` is not a member of the update command at all, so the
        // deserializer drops it. The guarantee being asserted is about the column, not the status.
        using var adminClient = await _auth.CreateRootAdminClientAsync();
        var user = await IdentityUserSeeder.CreateLoginableUserAsync(_factory, adminClient, "img-ignored");
        using var userClient = await _auth.CreateAuthenticatedClientAsync(user.Email, user.Password);

        var response = await userClient.PutAsJsonAsync(
            $"{TestConstants.IdentityBasePath}/profile", new
            {
                firstName = "Ada",
                imageUrl = "https://cdn.example.com/avatars/me.png",
            });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var profile = await userClient.GetAsync($"{TestConstants.IdentityBasePath}/profile");
        var dto = await profile.DeserializeAsync<UserDto>();
        dto.FirstName.ShouldBe("Ada");
        dto.ImageUrl.ShouldBeNull("only an upload may write this column");
    }

    #endregion
}
