using Boilerplate.BuildingBlocks.Shared.Security;
using Mediator;

namespace Boilerplate.Modules.Multitenancy.Contracts.v1.CreateTenant;

/// <summary>
/// Creates a tenant. The endpoint is idempotent, so this command is fingerprinted into a cache entry
/// that outlives the request — which is why its one credential-bearing member says so at the
/// property. <c>AdminPassword</c> is caught by name as well; the mark is the statement of intent,
/// and the name list is only the safety net for it.
/// </summary>
public sealed record CreateTenantCommand(
    string Id,
    string Name,
    string AdminEmail,
    [property: NotFingerprinted] string AdminPassword,
    string? Issuer,
    DateTime? ValidUpto = null) : ICommand<CreateTenantCommandResponse>;