namespace Boilerplate.BuildingBlocks.Eventing.Abstractions;

/// <summary>
/// Declares that an integration event belongs to the installation rather than to a tenant, so it is
/// dispatched with no tenant in scope.
///
/// The event-side counterpart of <c>[SystemJob]</c>, and for the same reason (ADR-0002): a null
/// <see cref="IIntegrationEvent.TenantId"/> used to mean "global" <i>and</i> "somebody forgot to set
/// it", and the two are indistinguishable at the point where it matters — inside a handler reading a
/// tenant-filtered table. Now global is a decision recorded in the type; an unmarked event published
/// without a tenant fails on dispatch.
///
/// A global handler that needs tenant data must enter each tenant explicitly through
/// <c>ITenantScope</c>.
/// </summary>
public interface IGlobalIntegrationEvent : IIntegrationEvent
{
}
