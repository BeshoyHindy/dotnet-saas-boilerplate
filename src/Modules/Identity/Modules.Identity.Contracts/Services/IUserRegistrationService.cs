using System.Security.Claims;

namespace Boilerplate.Modules.Identity.Contracts.Services;

/// <summary>
/// Service for user registration and external authentication.
/// </summary>
public interface IUserRegistrationService
{
    /// <summary>
    /// Registers a new user with password, as one transaction: the user, the <c>Basic</c> role, the
    /// tenant's default groups and the registration event either all exist or none of them do.
    /// The confirmation mail is sent from that event, so it cannot announce a sign-up that rolled
    /// back — and takes no <c>origin</c>: the link's base URL is configuration, resolved where the
    /// mail is built.
    /// </summary>
    Task<string> RegisterAsync(
        string firstName,
        string lastName,
        string email,
        string userName,
        string password,
        string confirmPassword,
        string phoneNumber,
        CancellationToken cancellationToken);

    /// <summary>
    /// Gets or creates a user from an external authentication principal.
    /// </summary>
    Task<string> GetOrCreateFromPrincipalAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms a user's email address.
    /// </summary>
    Task<string> ConfirmEmailAsync(string userId, string code, string tenant, CancellationToken cancellationToken);

    /// <summary>
    /// Administratively marks a user's email as confirmed without a confirmation token. Gated by the
    /// <c>Permissions.Users.ConfirmEmail</c> permission at the endpoint. Idempotent.
    /// </summary>
    Task AdminConfirmEmailAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Re-sends the email-confirmation link to a user who has not yet confirmed their address.
    /// <paramref name="origin"/> is the request base URL used to build the confirmation link.
    /// Throws if the user's email is already confirmed.
    /// </summary>
    Task ResendConfirmationEmailAsync(string userId, string origin, CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms a user's phone number.
    /// </summary>
    Task<string> ConfirmPhoneNumberAsync(string userId, string code, CancellationToken cancellationToken = default);
}