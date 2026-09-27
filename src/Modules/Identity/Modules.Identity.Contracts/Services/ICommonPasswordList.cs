namespace Boilerplate.Modules.Identity.Contracts.Services;

/// <summary>
/// The bundled list of common passwords (ASVS 5.0 V6.2.4). Identity checks every password it sets
/// against it through its own <c>IPasswordValidator</c>; this interface exists for the places that
/// take a password before <c>UserManager</c> sees it — the tenant admin password on
/// <c>CreateTenant</c>, and the demo password in the Migrator.
/// </summary>
/// <remarks>The list ships inside the Identity assembly. Nothing here calls an external service.</remarks>
public interface ICommonPasswordList
{
    /// <summary>
    /// <see langword="true"/> when <paramref name="password"/> is on the list, compared
    /// case-insensitively. An empty or <see langword="null"/> password is not "common" — refusing
    /// that is the length rule's job.
    /// </summary>
    bool Contains(string? password);
}
