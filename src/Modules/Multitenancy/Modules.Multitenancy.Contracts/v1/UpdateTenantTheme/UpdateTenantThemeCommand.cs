using Boilerplate.Modules.Multitenancy.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Multitenancy.Contracts.v1.UpdateTenantTheme;

public sealed record UpdateTenantThemeCommand(TenantThemeDto Theme) : ICommand;