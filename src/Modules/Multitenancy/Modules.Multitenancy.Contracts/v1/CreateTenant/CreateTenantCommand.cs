using Mediator;

namespace Boilerplate.Modules.Multitenancy.Contracts.v1.CreateTenant;

public sealed record CreateTenantCommand(
    string Id,
    string Name,
    string? ConnectionString,
    string AdminEmail,
    string AdminPassword,
    string? Issuer,
    DateTime? ValidUpto = null) : ICommand<CreateTenantCommandResponse>;