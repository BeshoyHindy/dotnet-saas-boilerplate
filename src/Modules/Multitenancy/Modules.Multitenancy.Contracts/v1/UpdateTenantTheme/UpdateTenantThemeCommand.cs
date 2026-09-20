using Boilerplate.Modules.Multitenancy.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Multitenancy.Contracts.v1.UpdateTenantTheme;

/// <summary>
/// Carries the theme's <b>write</b> model. Brand assets come in as bytes or a removal flag; the
/// asset URLs are response-only, because the server persists only the ones it issued (#83).
/// </summary>
public sealed record UpdateTenantThemeCommand(TenantThemeUpdateDto Theme) : ICommand;