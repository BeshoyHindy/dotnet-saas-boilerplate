using Mediator;

namespace Boilerplate.Modules.Identity.Contracts.v1.Users.ResendConfirmationEmail;

/// <summary>
/// Re-sends the email-confirmation link to an unconfirmed user. Gated by
/// <c>Permissions.Users.ConfirmEmail</c> at the endpoint. The link's base URL is not part of the
/// command: the handler reads it from <c>OriginOptions</c>, never from the request.
/// </summary>
public sealed record ResendConfirmationEmailCommand(string UserId) : ICommand<Unit>;
