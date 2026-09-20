using Integration.Tests.Infrastructure;
using Integration.Tests.Infrastructure.Extensions;
using Integration.Tests.Tests.Sessions;

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
