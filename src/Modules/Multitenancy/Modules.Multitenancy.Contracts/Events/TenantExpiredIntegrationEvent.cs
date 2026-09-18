using Boilerplate.BuildingBlocks.Eventing.Abstractions;

namespace Boilerplate.Modules.Multitenancy.Contracts.Events;

/// <summary>
/// Raised by the daily expiry scan when a tenant has passed <c>ValidUpto + grace</c> and is now
/// hard-blocked. Consumers notify the tenant that access is suspended until they renew.
/// </summary>
public sealed record TenantExpiredIntegrationEvent(
    Guid Id,
    DateTime OccurredOnUtc,
    string? TenantId,
    string CorrelationId,
    string Source,
    string TenantName,
    string AdminEmail,
    DateTime ValidUpto,
    DateTime GraceEndsUtc)
    : IIntegrationEvent;
