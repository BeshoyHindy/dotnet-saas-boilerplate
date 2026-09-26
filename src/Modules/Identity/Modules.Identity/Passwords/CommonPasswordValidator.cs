using Boilerplate.Modules.Identity.Contracts;
using Boilerplate.Modules.Identity.Contracts.Services;
using Boilerplate.Modules.Identity.Domain;
using Microsoft.AspNetCore.Identity;

namespace Boilerplate.Modules.Identity.Passwords;

/// <summary>
/// Refuses a password on the bundled common-password list (ASVS 5.0 V6.2.4). Registered on the
/// Identity builder in <see cref="IdentityModule"/>, so every <see cref="UserManager{TUser}"/> path
/// that sets a password runs it: registration, reset, change, and the tenant admin seed.
/// </summary>
/// <remarks>
/// The error is one generic sentence (<see cref="PasswordPolicy.CommonPasswordMessage"/>): it never
/// echoes the password, and it does not say where the list came from.
/// </remarks>
internal sealed class CommonPasswordValidator(ICommonPasswordList commonPasswords) : IPasswordValidator<AppUser>
{
    /// <summary>The <see cref="IdentityError.Code"/> of the refusal — stable, unlike the description.</summary>
    public const string ErrorCode = "CommonPassword";

    public Task<IdentityResult> ValidateAsync(UserManager<AppUser> manager, AppUser user, string? password) =>
        Task.FromResult(commonPasswords.Contains(password)
            ? IdentityResult.Failed(new IdentityError
            {
                Code = ErrorCode,
                Description = PasswordPolicy.CommonPasswordMessage,
            })
            : IdentityResult.Success);
}
