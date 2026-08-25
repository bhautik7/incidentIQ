using System.Text.Json.Serialization;

namespace IncidentIQ.Contracts;

/// <summary>
/// The single shape every message on every topic uses.
///
/// A uniform envelope means routing, tracing, dead-lettering and replay can be
/// written once and work for all seven topics. The part that varies -
/// <see cref="Payload"/> - is the only part a handler has to know about.
///
/// Serialised with camelCase property names. C# uses PascalCase, Python uses
/// snake_case, and neither dictates the wire format: the JSON is the contract,
/// and each language maps to its own idiom on the way in and out.
/// </summary>
public sealed record EventEnvelope<TPayload>
{
    /// <summary>
    /// Unique id for this event. The idempotency key: a consumer that has
    /// already handled this id must treat a redelivery as a no-op. Generated
    /// once by the producer and preserved across retries and DLQ replays -
    /// regenerating it would defeat the entire mechanism.
    /// </summary>
    [JsonPropertyName("eventId")]
    public required Guid EventId { get; init; }

    /// <summary>
    /// What happened, e.g. "log.received". Lets a consumer route without
    /// deserialising the payload, and lets one topic carry several event types.
    /// </summary>
    [JsonPropertyName("eventType")]
    public required string EventType { get; init; }

    /// <summary>
    /// Schema version of <see cref="Payload"/>, starting at 1. Additive changes
    /// keep the version; a breaking change increments it, and consumers that do
    /// not understand a version must dead-letter rather than guess.
    /// </summary>
    [JsonPropertyName("eventVersion")]
    public required int EventVersion { get; init; }

    /// <summary>
    /// When the thing described actually happened - not when the message was
    /// published, and not when it was consumed. Always UTC.
    /// </summary>
    [JsonPropertyName("occurredAt")]
    public required DateTimeOffset OccurredAt { get; init; }

    /// <summary>
    /// The owning organization. Present on every event so a consumer can set its
    /// tenant scope before touching the database, and so a mis-routed message is
    /// detectable rather than silently written to the wrong tenant.
    /// </summary>
    [JsonPropertyName("tenantId")]
    public required Guid TenantId { get; init; }

    /// <summary>
    /// Ties together every event caused by one original action. A log batch, the
    /// normalised events derived from it, the incident that opened, and the AI
    /// analysis all carry the same value - which is what makes a single
    /// end-to-end trace possible across five processes.
    /// </summary>
    [JsonPropertyName("correlationId")]
    public required Guid CorrelationId { get; init; }

    [JsonPropertyName("payload")]
    public required TPayload Payload { get; init; }

    /// <summary>
    /// Builds an envelope with the fields that are always mechanical, so a
    /// caller only supplies what is genuinely specific to the event.
    /// </summary>
    public static EventEnvelope<TPayload> Create(
        string eventType,
        Guid tenantId,
        TPayload payload,
        Guid? correlationId = null,
        DateTimeOffset? occurredAt = null,
        int eventVersion = 1) => new()
    {
        // Version 7: time-ordered, so ids sort by creation and index well if
        // they are ever persisted.
        EventId = Guid.CreateVersion7(),
        EventType = eventType,
        EventVersion = eventVersion,
        OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
        TenantId = tenantId,
        CorrelationId = correlationId ?? Guid.CreateVersion7(),
        Payload = payload
    };
}
