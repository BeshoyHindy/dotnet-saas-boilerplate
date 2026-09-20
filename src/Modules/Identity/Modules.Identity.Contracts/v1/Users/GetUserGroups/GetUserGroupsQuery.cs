using Boilerplate.Modules.Identity.Contracts.DTOs;
using Mediator;

namespace Boilerplate.Modules.Identity.Contracts.v1.Users.GetUserGroups;

public sealed record GetUserGroupsQuery(string UserId) : IQuery<IEnumerable<GroupDto>>;