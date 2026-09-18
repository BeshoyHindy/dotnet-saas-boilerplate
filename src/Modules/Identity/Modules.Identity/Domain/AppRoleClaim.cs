using Microsoft.AspNetCore.Identity;

namespace Boilerplate.Modules.Identity.Domain;

public class AppRoleClaim : IdentityRoleClaim<string>
{
    public string? CreatedBy { get; init; }
    public DateTimeOffset CreatedOn { get; init; }
}