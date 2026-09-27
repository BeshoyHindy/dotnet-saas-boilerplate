using System.Text.Json.Serialization;

namespace Boilerplate.Modules.Identity.Contracts.DTOs;

public class UserDto
{
    public string? Id { get; set; }

    public string? UserName { get; set; }

    public string? FirstName { get; set; }

    public string? LastName { get; set; }

    public string? Email { get; set; }

    public bool IsActive { get; set; } = true;

    public bool EmailConfirmed { get; set; }

    public string? PhoneNumber { get; set; }

    public string? ImageUrl { get; set; }

    /// <summary>Whether the user has enrolled in TOTP-based two-factor authentication.</summary>
    public bool TwoFactorEnabled { get; set; }

    /// <summary>
    /// The row's version (<c>AppUser.ConcurrencyStamp</c>). Never serialized: <c>GET /identity/profile</c>
    /// sends it as the <c>ETag</c> header instead, and the other endpoints returning this DTO do not
    /// expose it at all (#107).
    /// </summary>
    [JsonIgnore]
    public string? ConcurrencyStamp { get; set; }
}