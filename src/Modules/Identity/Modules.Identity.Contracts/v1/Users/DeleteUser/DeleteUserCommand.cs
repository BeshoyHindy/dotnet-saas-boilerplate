using Mediator;

namespace Boilerplate.Modules.Identity.Contracts.v1.Users.DeleteUser;

public sealed record DeleteUserCommand(string Id) : ICommand<Unit>;