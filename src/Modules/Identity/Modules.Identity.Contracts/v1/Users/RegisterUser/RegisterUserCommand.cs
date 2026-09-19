using Mediator;

namespace Boilerplate.Modules.Identity.Contracts.v1.Users.RegisterUser;

/// <summary>
/// Registers a user and mails them a confirmation link. The link's base URL is not part of the
/// command: the handler reads it from <c>OriginOptions</c> so no caller-supplied value (body, header
/// or <c>Request.Host</c>) can ever steer where the confirmation email points.
/// </summary>
public class RegisterUserCommand : ICommand<RegisterUserResponse>
{
    public string FirstName { get; set; } = default!;
    public string LastName { get; set; } = default!;
    public string Email { get; set; } = default!;
    public string UserName { get; set; } = default!;
    public string Password { get; set; } = default!;
    public string ConfirmPassword { get; set; } = default!;
    public string? PhoneNumber { get; set; }
}