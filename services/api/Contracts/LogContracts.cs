using System.Text.Json.Serialization;

namespace IncidentIQ.Api.Contracts;

/// <summary>
/// One page of a stream, plus where to continue from.
///
/// Deliberately not <see cref="PagedResult{T}"/>: that shape carries a total
/// count and a page number, and neither is honest here. Counting matching rows
/// in a log table means scanning them, the answer changes between the count and
/// the fetch because new lines keep arriving, and "page 4 of 812" invites
/// jumping to a page that will hold different rows by the time it loads.
///
/// A cursor says the only thing that stays true: here is what comes next.
/// </summary>
public sealed record CursorPage<T>
{
    [JsonPropertyName("items")]
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>Opaque. Null when the caller has reached the end of the window.</summary>
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

/// <summary>
/// A log line as the explorer renders it.
///
/// The stack trace and structured properties are included because expanding a
/// row must not cost another request - during an outage the expand is how
/// someone reads the line, and a spinner inside a row someone just clicked is
/// worse than a slightly larger page.
/// </summary>
public sealed record LogEntry
{
    [JsonPropertyName("id")]
    public required long Id { get; init; }

    [JsonPropertyName("occurredAt")]
    public required DateTimeOffset OccurredAt { get; init; }

    [JsonPropertyName("receivedAt")]
    public required DateTimeOffset ReceivedAt { get; init; }

    [JsonPropertyName("level")]
    public required string Level { get; init; }

    [JsonPropertyName("service")]
    public required string Service { get; init; }

    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

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

    /// <summary>Raw jsonb, passed through untouched for the row's JSON view.</summary>
    [JsonPropertyName("properties")]
    public string? Properties { get; init; }

    /// <summary>The fingerprint this line was grouped under, for "filter by this".</summary>
    [JsonPropertyName("fingerprint")]
    public string? Fingerprint { get; init; }

    /// <summary>Set when an incident is currently open for this line's pattern, so the row can link to it.</summary>
    [JsonPropertyName("incidentId")]
    public Guid? IncidentId { get; init; }
}

/// <summary>
/// What the explorer can see, so the UI can say so rather than implying it is
/// showing everything ever logged.
/// </summary>
public sealed record LogWindow
{
    [JsonPropertyName("retentionHours")]
    public required int RetentionHours { get; init; }

    /// <summary>The oldest line actually held, which is what the user can really reach.</summary>
    [JsonPropertyName("oldestAvailableAt")]
    public DateTimeOffset? OldestAvailableAt { get; init; }
}

public sealed record LogSearchResult
{
    [JsonPropertyName("page")]
    public required CursorPage<LogEntry> Page { get; init; }

    [JsonPropertyName("window")]
    public required LogWindow Window { get; init; }
}
