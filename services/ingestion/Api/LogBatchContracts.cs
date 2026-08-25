using System.Text.Json.Serialization;

namespace IncidentIQ.Ingestion.Api;

/// <summary>
/// The public HTTP request body.
///
/// Deliberately separate from the Kafka contract in IncidentIQ.Contracts. This
/// is a customer-facing API that agents and SDKs are written against, so it
/// changes slowly and only in backward-compatible ways. The internal event
/// schema is free to change whenever the pipeline needs it to. Sharing one type
/// would chain them together.
/// </summary>
public sealed record LogBatchRequest
{
    [JsonPropertyName("events")]
    public List<LogEventRequest>? Events { get; init; }
}

public sealed record LogEventRequest
{
    /// <summary>
    /// Client-generated idempotency key, reused across the client's own
    /// retries. Optional, but a client that omits it gives up exactly-once
    /// semantics: a retried HTTP request becomes duplicate log events, because
    /// there is nothing for the database's unique index to collide on.
    /// </summary>
    [JsonPropertyName("eventId")]
    public Guid? EventId { get; init; }

    /// <summary>Service name as the client knows it, e.g. "payments-api".</summary>
    [JsonPropertyName("service")]
    public string? Service { get; init; }

    [JsonPropertyName("environment")]
    public string? Environment { get; init; }

    /// <summary>When the application logged it. Required: server receipt time is not a substitute.</summary>
    [JsonPropertyName("timestamp")]
    public DateTimeOffset? Timestamp { get; init; }

    /// <summary>Trace, Debug, Information, Warning, Error or Fatal. Common aliases are accepted.</summary>
    [JsonPropertyName("severity")]
    public string? Severity { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }

    [JsonPropertyName("exceptionType")]
    public string? ExceptionType { get; init; }

    [JsonPropertyName("stackTrace")]
    public string? StackTrace { get; init; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; init; }

    [JsonPropertyName("spanId")]
    public string? SpanId { get; init; }

    [JsonPropertyName("host")]
    public string? Host { get; init; }

    /// <summary>Arbitrary structured context. Becomes log_events.properties.</summary>
    [JsonPropertyName("metadata")]
    public Dictionary<string, string>? Metadata { get; init; }
}

/// <summary>
/// Returned on 202. Reports per-event outcomes rather than a bare status code,
/// because a partially valid batch is the normal case and a client needs to
/// know which events it must fix - not merely that something was wrong.
/// </summary>
public sealed record LogBatchResponse
{
    [JsonPropertyName("accepted")]
    public required int Accepted { get; init; }

    [JsonPropertyName("rejected")]
    public required int Rejected { get; init; }

    [JsonPropertyName("correlationId")]
    public required Guid CorrelationId { get; init; }

    /// <summary>One entry per rejected event, identified by its index in the submitted array.</summary>
    [JsonPropertyName("errors")]
    public IReadOnlyList<LogEventError> Errors { get; init; } = [];
}

public sealed record LogEventError
{
    /// <summary>Zero-based index in the submitted "events" array.</summary>
    [JsonPropertyName("index")]
    public required int Index { get; init; }

    [JsonPropertyName("field")]
    public required string Field { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}
