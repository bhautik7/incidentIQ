using System.Text.Json.Serialization;

namespace IncidentIQ.Contracts.Payloads;

/// <summary>
/// One log line as accepted from a client, before any processing.
/// Travels on <c>logs.raw</c>.
///
/// Service and environment are the client's own names ("payments-api",
/// "production"), not database ids. Ingestion must not have to resolve them -
/// that would put a database lookup on the write path, which is exactly what
/// the architecture keeps it away from. Resolution happens in the processor.
/// </summary>
public sealed record LogReceived
{
    /// <summary>
    /// Generated once by the producing client and reused across its own HTTP
    /// retries. Becomes log_events.event_id, whose unique index makes a
    /// duplicate a no-op enforced by the database.
    /// </summary>
    [JsonPropertyName("logEventId")]
    public required Guid LogEventId { get; init; }

    [JsonPropertyName("service")]
    public required string Service { get; init; }

    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    [JsonPropertyName("level")]
    public required string Level { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    /// <summary>When the application logged it, as opposed to when we received it.</summary>
    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

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

    /// <summary>Arbitrary structured properties from the log call.</summary>
    [JsonPropertyName("properties")]
    public IReadOnlyDictionary<string, string>? Properties { get; init; }
}

/// <summary>
/// A log event after masking and fingerprinting. Travels on
/// <c>logs.normalized</c>.
///
/// The fingerprint is the whole point: it is what turns 4,200 near-identical
/// lines into one incident. Emitting it as its own event means the expensive,
/// rule-driven normalisation step can be replayed and changed independently of
/// ingestion.
/// </summary>
public sealed record LogNormalized
{
    [JsonPropertyName("logEventId")]
    public required Guid LogEventId { get; init; }

    [JsonPropertyName("service")]
    public required string Service { get; init; }

    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    [JsonPropertyName("level")]
    public required string Level { get; init; }

    /// <summary>SHA-256 over the normalised message and top stack frames, 64 hex characters.</summary>
    [JsonPropertyName("fingerprint")]
    public required string Fingerprint { get; init; }

    /// <summary>The message with GUIDs, numbers, IPs and other variable parts masked.</summary>
    [JsonPropertyName("messageTemplate")]
    public required string MessageTemplate { get; init; }

    /// <summary>The original, unmasked message, kept so the UI can show something concrete.</summary>
    [JsonPropertyName("sampleMessage")]
    public required string SampleMessage { get; init; }

    [JsonPropertyName("exceptionType")]
    public string? ExceptionType { get; init; }

    [JsonPropertyName("timestamp")]
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>
    /// HTTP status, when the event carried one.
    ///
    /// Extracted during normalisation rather than left in the properties bag,
    /// because the server-error spike rule aggregates across fingerprints and
    /// needs a field it can sum without deserialising every payload. Additive
    /// and optional, so older producers stay compatible.
    /// </summary>
    [JsonPropertyName("httpStatusCode")]
    public int? HttpStatusCode { get; init; }
}

/// <summary>
/// A message that could not be processed. Travels on <c>logs.failed</c>.
///
/// Carries the original JSON verbatim so a replay is byte-identical, plus
/// enough context to work out what went wrong without hunting through logs.
/// Never replayed automatically - it failed for a reason, and automatic replay
/// rediscovers the same bug forever.
/// </summary>
public sealed record LogFailed
{
    [JsonPropertyName("sourceTopic")]
    public required string SourceTopic { get; init; }

    [JsonPropertyName("sourcePartition")]
    public required int SourcePartition { get; init; }

    [JsonPropertyName("sourceOffset")]
    public required long SourceOffset { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("exceptionType")]
    public string? ExceptionType { get; init; }

    [JsonPropertyName("attempts")]
    public required int Attempts { get; init; }

    [JsonPropertyName("failedAt")]
    public required DateTimeOffset FailedAt { get; init; }

    /// <summary>The original message body, unmodified.</summary>
    [JsonPropertyName("originalPayload")]
    public required string OriginalPayload { get; init; }
}
