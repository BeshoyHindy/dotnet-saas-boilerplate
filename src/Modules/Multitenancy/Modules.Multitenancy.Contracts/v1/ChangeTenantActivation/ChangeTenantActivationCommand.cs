using Boilerplate.Modules.Multitenancy.Contracts.Dtos;
using Mediator;

namespace Boilerplate.Modules.Multitenancy.Contracts.v1.ChangeTenantActivation;

public sealed record ChangeTenantActivationCommand(string TenantId, bool IsActive)
    : ICommand<TenantLifecycleResultDto>;