using Boilerplate.Modules.Identity.Contracts.DTOs;
using Mediator;

namespace Boilerplate.Modules.Identity.Contracts.v1.Users.GetUsers;

public sealed record GetUsersQuery : IQuery<List<UserDto>>;