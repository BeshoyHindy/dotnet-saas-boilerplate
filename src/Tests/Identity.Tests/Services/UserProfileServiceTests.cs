using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.BuildingBlocks.Shared.Storage;
using Boilerplate.BuildingBlocks.Storage;
using Boilerplate.BuildingBlocks.Storage.Services;
using Boilerplate.BuildingBlocks.Web.Origin;
using Boilerplate.Modules.Identity.Domain;
using Boilerplate.Modules.Identity.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Identity.Tests.Services;

/// <summary>
/// The avatar half of <c>UserProfileService.UpdateAsync</c> — the only thing that writes
/// <c>AppUser.ImageUrl</c> since #83 removed "set my avatar URL".
///
/// <para>What these pin is <b>who</b> the upload and the delete are scoped to. The tenant prefix
/// cannot separate two users of the same tenant, so the delete that follows a replace has to be
/// owner-scoped — otherwise a column still holding a neighbour's avatar URL (an old row; there is no
/// migration) turns the next save into a deletion of their bytes.</para>
/// </summary>
public sealed class UserProfileServiceTests
{
    private const string TenantId = "acme";
    private const string UserId = "8f1b6f2e-0a4c-4b3f-9f64-1f2b0c1d2e3f";

    private readonly UserManager<AppUser> _userManager;
    private readonly SignInManager<AppUser> _signInManager;
    private readonly IStorageService _storage = Substitute.For<IStorageService>();
    private readonly AppUser _user = new() { Id = UserId, UserName = "ada", Email = "ada@example.com" };

    public UserProfileServiceTests()
    {
        _userManager = Substitute.For<UserManager<AppUser>>(
            Substitute.For<IUserStore<AppUser>>(), null, null, null, null, null, null, null, null);
        _signInManager = Substitute.For<SignInManager<AppUser>>(
            _userManager,
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<IUserClaimsPrincipalFactory<AppUser>>(),
            null, null, null, null);

        _userManager.FindByIdAsync(UserId).Returns(_user);
        _userManager.UpdateAsync(Arg.Any<AppUser>()).Returns(IdentityResult.Success);
        _storage.UploadAsync<AppUser>(
                Arg.Any<FileUploadRequest>(), Arg.Any<FileType>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("https://cdn.example.com/uploads/tenants/acme/appuser/" + UserId + "/new.png");
    }

    private UserProfileService CreateSut()
    {
        var tenantAccessor = Substitute.For<IMultiTenantContextAccessor<AppTenantInfo>>();
        var context = Substitute.For<IMultiTenantContext<AppTenantInfo>>();
        context.TenantInfo.Returns(new AppTenantInfo(TenantId, TenantId, "Acme"));
        tenantAccessor.MultiTenantContext.Returns(context);

        return new UserProfileService(
            _userManager,
            _signInManager,
            _storage,
            tenantAccessor,
            Options.Create(new OriginOptions()),
            Substitute.For<IHttpContextAccessor>());
    }

    private static FileUploadRequest Png() => new()
    {
        FileName = "avatar.png",
        ContentType = "image/png",
        Data = [137, 80, 78, 71],
    };

    [Fact]
    public async Task UpdateAsync_Should_UploadTheAvatar_UnderTheUsersOwnOwnerSegment()
    {
        var sut = CreateSut();

        await sut.UpdateAsync(UserId, "Ada", "Lovelace", "123", Png(), deleteCurrentImage: false, CancellationToken.None);

        await _storage.Received(1).UploadAsync<AppUser>(
            Arg.Any<FileUploadRequest>(), FileType.Image, UserId, Arg.Any<CancellationToken>());
        _user.ImageUrl!.ToString().ShouldBe("https://cdn.example.com/uploads/tenants/acme/appuser/" + UserId + "/new.png");
    }

    [Fact]
    public async Task UpdateAsync_Should_DropThePreviousAvatar_OwnerScoped_NotTenantWide()
    {
        // The previous value here is another user's avatar — the state an existing row can be in,
        // because until #83 any string was accepted. The owner-scoped delete refuses it (skips and
        // logs); the tenant-wide one would have deleted it, which is the bug.
        const string neighboursAvatar = "https://cdn.example.com/uploads/tenants/acme/appuser/someone-else/theirs.png";
        _user.ImageUrl = new Uri(neighboursAvatar);
        var sut = CreateSut();

        await sut.UpdateAsync(UserId, "Ada", "Lovelace", "123", Png(), deleteCurrentImage: false, CancellationToken.None);

        await _storage.Received(1).RemoveIfOwnedAsync<AppUser>(
            neighboursAvatar, UserId, Arg.Any<CancellationToken>());
        await _storage.DidNotReceive().RemoveIfOwnedAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_Should_ClearTheColumn_AndDropTheObject_OwnerScoped_When_DeleteRequested()
    {
        const string mine = "https://cdn.example.com/uploads/tenants/acme/appuser/" + UserId + "/old.png";
        _user.ImageUrl = new Uri(mine);
        var sut = CreateSut();

        await sut.UpdateAsync(UserId, "Ada", "Lovelace", "123", null!, deleteCurrentImage: true, CancellationToken.None);

        await _storage.Received(1).RemoveIfOwnedAsync<AppUser>(mine, UserId, Arg.Any<CancellationToken>());
        _user.ImageUrl.ShouldBeNull();
    }

    [Fact]
    public async Task UpdateAsync_Should_TouchNoStorage_When_OnlyTextFieldsChange()
    {
        _user.ImageUrl = new Uri("https://cdn.example.com/uploads/tenants/acme/appuser/" + UserId + "/keep.png");
        var sut = CreateSut();

        await sut.UpdateAsync(UserId, "Ada", "Lovelace", "123", null!, deleteCurrentImage: false, CancellationToken.None);

        _storage.ReceivedCalls().ShouldBeEmpty();
        _user.ImageUrl.ShouldNotBeNull();
    }
}
