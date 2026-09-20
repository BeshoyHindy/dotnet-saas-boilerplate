using Boilerplate.Modules.Multitenancy.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Multitenancy.Contracts.v1.GetTenantTheme;

public sealed record GetTenantThemeQuery : IQuery<TenantThemeDto>;