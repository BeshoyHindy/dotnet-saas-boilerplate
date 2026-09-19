using Finbuckle.MultiTenant.Abstractions;
using Boilerplate.BuildingBlocks.Core.Exceptions;
using Boilerplate.BuildingBlocks.Shared.Multitenancy;
using Boilerplate.BuildingBlocks.Shared.Storage;
using Boilerplate.BuildingBlocks.Storage;
using Boilerplate.BuildingBlocks.Storage.Services;
using Boilerplate.BuildingBlocks.Web.Origin;
using Boilerplate.Modules.Identity.Contracts.DTOs;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Boilerplate.Modules.Identity.Services;

internal sealed class UserProfileService(
    UserManager<AppUser> userManager,
    SignInManager<AppUser> signInManager,
    IStorageService storageService,
    IMultiTenantContextAccessor<AppTenantInfo> multiTenantContextAccessor,
    IOptions<OriginOptions> originOptions,
    IHttpContextAccessor httpContextAccessor) : IUserProfileService
{
    private readonly Uri? _originUrl = originOptions.Value.OriginUrl;

    public async Task<UserDto> GetAsync(string userId, CancellationToken cancellationToken)
    {
        // Relies on Finbuckle's tenant filter — callers can only ever read
        // their own user record, which is in the request's resolved tenant.
        var user = await userManager.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .FirstOrDefaultAsync(cancellationToken);

        _ = user ?? throw new NotFoundException("user not found");

        return new UserDto
        {
            Id = user.Id,
            Email = user.Email,
            UserName = user.UserName,
            FirstName = user.FirstName,
            LastName = user.LastName,
            ImageUrl = ResolveImageUrl(user.ImageUrl),
            IsActive = user.IsActive,
            EmailConfirmed = user.EmailConfirmed,
            PhoneNumber = user.PhoneNumber,
            TwoFactorEnabled = user.TwoFactorEnabled,
        };
    }

    public Task<int> GetCountAsync(CancellationToken cancellationToken) =>
        userManager.Users.AsNoTracking().CountAsync(cancellationToken);

    public async Task<List<UserDto>> GetListAsync(CancellationToken cancellationToken)
    {
        var users = await userManager.Users.AsNoTracking().ToListAsync(cancellationToken);
        var result = new List<UserDto>(users.Count);
        foreach (var user in users)
        {
            result.Add(new UserDto
            {
                Id = user.Id,
                Email = user.Email,
                UserName = user.UserName,
                FirstName = user.FirstName,
                LastName = user.LastName,
                ImageUrl = ResolveImageUrl(user.ImageUrl),
                IsActive = user.IsActive
            });
        }

        return result;
    }

    public async Task UpdateAsync(string userId, string firstName, string lastName, string phoneNumber, FileUploadRequest image, bool deleteCurrentImage, CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(userId);

        _ = user ?? throw new NotFoundException("user not found");

        Uri imageUri = user.ImageUrl ?? null!;
        // image is optional: text-only edits forward a null FileUploadRequest, so guard before
        // dereferencing Data or the common no-image update path NREs.
        //
        // The previous value is dropped with RemoveIfOwnedAsync, not RemoveAsync: ImageUrl is a URL
        // column, and what is in it may not be one of this tenant's keys at all — PUT
        // /identity/profile/image lets a user store any URL, and a development database predating
        // tenant-prefixed keys still holds flat `uploads/{type}/…` values. Neither is ours to
        // delete, and neither should turn saving a profile into a 500.
        if (image?.Data != null)
        {
            var imageString = await storageService.UploadAsync<AppUser>(image, FileType.Image, cancellationToken);
            user.ImageUrl = new Uri(imageString, UriKind.RelativeOrAbsolute);

            // Unconditionally, not `if (deleteCurrentImage)`: the validator rejects a request that
            // sets both, so that condition was never true and every avatar change orphaned its
            // predecessor's bytes forever. Replacing the column value is what makes the old object
            // unreachable, so dropping it here is the whole of its lifecycle — the same thing
            // TenantThemeService has always done for a brand asset.
            if (imageUri != null)
            {
                await storageService.RemoveIfOwnedAsync(imageUri.ToString(), cancellationToken);
            }
        }
        else if (deleteCurrentImage && imageUri != null)
        {
            await storageService.RemoveIfOwnedAsync(imageUri.ToString(), cancellationToken);
            user.ImageUrl = null;
        }

        user.FirstName = firstName;
        user.LastName = lastName;
        string? currentPhoneNumber = await userManager.GetPhoneNumberAsync(user);
        if (phoneNumber != currentPhoneNumber)
        {
            await userManager.SetPhoneNumberAsync(user, phoneNumber);
        }

        var result = await userManager.UpdateAsync(user);
        await signInManager.RefreshSignInAsync(user);

        if (!result.Succeeded)
        {
            throw new CustomException("Update profile failed");
        }
    }

    public async Task SetImageUrlAsync(string userId, string? imageUrl, CancellationToken cancellationToken)
    {
        EnsureValidTenant();
        var user = await userManager.FindByIdAsync(userId)
            ?? throw new NotFoundException("user not found");

        user.ImageUrl = string.IsNullOrWhiteSpace(imageUrl)
            ? null
            : new Uri(imageUrl, UriKind.RelativeOrAbsolute);

        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            throw new CustomException("Update profile image failed");
        }

        await signInManager.RefreshSignInAsync(user);
    }

    public async Task<bool> ExistsWithEmailAsync(string email, string? exceptId = null, CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();
        return await userManager.FindByEmailAsync(email.Normalize()) is AppUser user && user.Id != exceptId;
    }

    public async Task<bool> ExistsWithNameAsync(string name, CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();
        return await userManager.FindByNameAsync(name) is not null;
    }

    public async Task<bool> ExistsWithPhoneNumberAsync(string phoneNumber, string? exceptId = null, CancellationToken cancellationToken = default)
    {
        EnsureValidTenant();
        return await userManager.Users.FirstOrDefaultAsync(x => x.PhoneNumber == phoneNumber, cancellationToken) is AppUser user && user.Id != exceptId;
    }

    private void EnsureValidTenant()
    {
        if (string.IsNullOrWhiteSpace(multiTenantContextAccessor?.MultiTenantContext?.TenantInfo?.Id))
        {
            throw new UnauthorizedException("invalid tenant");
        }
    }

    private string? ResolveImageUrl(Uri? imageUrl)
    {
        if (imageUrl is null)
        {
            return null;
        }

        // Absolute URLs (e.g., S3) pass through unchanged.
        if (imageUrl.IsAbsoluteUri)
        {
            return imageUrl.ToString();
        }

        // For relative paths from local storage, prefix with the API origin and wwwroot.
        if (_originUrl is null)
        {
            var request = httpContextAccessor.HttpContext?.Request;
            if (request is not null && !string.IsNullOrWhiteSpace(request.Scheme) && request.Host.HasValue)
            {
                var baseUri = $"{request.Scheme}://{request.Host.Value}{request.PathBase}";
                var relativePath = imageUrl.ToString().TrimStart('/');
                return $"{baseUri.TrimEnd('/')}/{relativePath}";
            }

            return imageUrl.ToString();
        }

        var originRelativePath = imageUrl.ToString().TrimStart('/');
        return $"{_originUrl.AbsoluteUri.TrimEnd('/')}/{originRelativePath}";
    }
}