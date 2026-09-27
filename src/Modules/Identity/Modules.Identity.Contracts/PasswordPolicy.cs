namespace Boilerplate.Modules.Identity.Contracts;

/// <summary>
/// The password policy every Identity path enforces, in the terms another module needs to repeat
/// it. It is ASVS 5.0 Level 1: a length floor (V6.2.1) and a common-password list (V6.2.4,
/// <see cref="Services.ICommonPasswordList"/>), and <b>no composition rules</b> (V6.2.5) — no
/// required digit, case or symbol.
/// </summary>
/// <remarks>
/// It lives here rather than in Identity's runtime because the tenant admin's password is taken by
/// Multitenancy's <c>CreateTenant</c> and only reaches <c>UserManager</c> later, in the background
/// seed step. That request has to refuse a password the seed would refuse, with the same number.
/// </remarks>
public static class PasswordPolicy
{
    /// <summary>The shortest password Identity accepts.</summary>
    public const int MinimumLength = 10;

    /// <summary>
    /// The one message for a password found on the common-password list. Deliberately generic, and
    /// never carries the password itself.
    /// </summary>
    public const string CommonPasswordMessage = "This password is too common. Choose a different one.";
}
