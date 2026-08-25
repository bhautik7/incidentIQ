using System.Text.Json;
using System.Text.Json.Serialization;

namespace IncidentIQ.Contracts;

/// <summary>
/// One serializer configuration, used by producers, consumers and tests alike.
///
/// If a producer and a consumer configure JSON differently, the mismatch shows
/// up as a null field in production rather than as a build error - so the
/// options live here and are never constructed ad hoc.
/// </summary>
public static class EventJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        // Property names are set explicitly with [JsonPropertyName] rather than
        // inferred from a policy: the wire format should not change because
        // someone renames a C# property.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,

        // Unknown fields are ignored rather than rejected. This is what makes an
        // additive change - a new optional field from a newer producer -
        // deployable without a lockstep release of every consumer.
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,

        PropertyNameCaseInsensitive = false,
        WriteIndented = false
    };

    public static byte[] SerializeToUtf8Bytes<TPayload>(EventEnvelope<TPayload> envelope) =>
        JsonSerializer.SerializeToUtf8Bytes(envelope, Options);

    public static string Serialize<TPayload>(EventEnvelope<TPayload> envelope) =>
        JsonSerializer.Serialize(envelope, Options);

    public static EventEnvelope<TPayload> Deserialize<TPayload>(ReadOnlySpan<byte> utf8Json) =>
        JsonSerializer.Deserialize<EventEnvelope<TPayload>>(utf8Json, Options)
        ?? throw new JsonException("Event envelope deserialised to null.");

    public static EventEnvelope<TPayload> Deserialize<TPayload>(string json) =>
        JsonSerializer.Deserialize<EventEnvelope<TPayload>>(json, Options)
        ?? throw new JsonException("Event envelope deserialised to null.");
}
