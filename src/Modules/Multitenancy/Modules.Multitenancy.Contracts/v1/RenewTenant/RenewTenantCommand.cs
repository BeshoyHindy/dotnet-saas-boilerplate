using Mediator;

namespace Boilerplate.Modules.Multitenancy.Contracts.v1.RenewTenant;

/// <summary>
/// Extends a tenant's validity. When <see cref="Months"/> is null the configured default validity
/// term is applied; remaining time is stacked on rather than discarded.
/// </summary>
public sealed record RenewTenantCommand(string TenantId, int? Months = null)
    : ICommand<RenewTenantCommandResponse>;
