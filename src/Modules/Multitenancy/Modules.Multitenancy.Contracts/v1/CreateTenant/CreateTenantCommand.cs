using Boilerplate.BuildingBlocks.Shared.Security;
using Mediator;

namespace Boilerplate.Modules.Multitenancy.Contracts.v1.CreateTenant;

/// <summary>
/// Creates a tenant. The endpoint is idempotent, so this command is fingerprinted into a cache entry
/// that outlives the request — which is why its two credential-bearing members say so at the
/// property. <c>AdminPassword</c> is caught by name as well; <c>ConnectionString</c> is a database
/// credential that no name rule caught until it was one, and marking it is the statement of intent
/// the name list is only the safety net for.
/// </summary>
public sealed record CreateTenantCommand(
    string Id,
    string Name,
    [property: NotFingerprinted] string? ConnectionString,
    string AdminEmail,
    [property: NotFingerprinted] string AdminPassword,
    string? Issuer,
    DateTime? ValidUpto = null) : ICommand<CreateTenantCommandResponse>;