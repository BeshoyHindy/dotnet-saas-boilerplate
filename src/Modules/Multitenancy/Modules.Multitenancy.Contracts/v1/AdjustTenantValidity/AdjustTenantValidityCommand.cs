using Mediator;

namespace Boilerplate.Modules.Multitenancy.Contracts.v1.AdjustTenantValidity;

/// <summary>
/// Operator override that sets a tenant's validity to an explicit date. Intended for comps,
/// support extensions, or immediate expiry. May move the date backward, unlike renewal.
/// </summary>
public sealed record AdjustTenantValidityCommand(string TenantId, DateTime ValidUpto)
    : ICommand<AdjustTenantValidityCommandResponse>;
