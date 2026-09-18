namespace Boilerplate.BuildingBlocks.Eventing.Abstractions;

/// <summary>
/// Serializes and deserializes integration events for transport and storage (outbox).
/// </summary>
public interface IEventSerializer
{
    string Serialize(IIntegrationEvent @event);

    IIntegrationEvent? Deserialize(string payload, string eventTypeName);
}