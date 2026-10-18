using System.Text.Json.Serialization;

namespace Cyllio.Common.Messaging;

public sealed record InfrastructureProbe(
    [property: JsonPropertyName("eventId")] string EventId)
{
    public const string EventName = "InfrastructureProbe";

    [JsonPropertyName("eventType")]
    public string EventType => EventName;
}
