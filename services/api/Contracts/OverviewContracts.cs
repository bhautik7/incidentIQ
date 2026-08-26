using System.Text.Json.Serialization;

namespace IncidentIQ.Api.Contracts;

/// <summary>
/// One KPI tile.
///
/// A bare number is not information: 7 active incidents is reassuring after 20
/// and alarming after 2. Every tile therefore carries what it was, and a
/// sparkline for the shape in between - the delta says whether to worry, the
/// sparkline says whether it is still moving.
/// </summary>
public sealed record MetricSummary
{
    [JsonPropertyName("value")]
    public required double Value { get; init; }

    /// <summary>The same measure over the immediately preceding window of equal length.</summary>
    [JsonPropertyName("previousValue")]
    public required double PreviousValue { get; init; }

    /// <summary>
    /// Null when the previous window was zero. A change from nothing is not a
    /// percentage, and rendering "+∞%" or "+100%" would both be lies.
    /// </summary>
    [JsonPropertyName("changePercent")]
    public double? ChangePercent { get; init; }

    /// <summary>Bucketed history for the sparkline. Same buckets as the timeline.</summary>
    [JsonPropertyName("series")]
    public IReadOnlyList<double> Series { get; init; } = [];
}

/// <summary>One point on the health timeline.</summary>
public sealed record TimelinePoint
{
    [JsonPropertyName("bucketStart")]
    public required DateTimeOffset BucketStart { get; init; }

    [JsonPropertyName("errorEvents")]
    public required long ErrorEvents { get; init; }

    [JsonPropertyName("warningEvents")]
    public required long WarningEvents { get; init; }
}

/// <summary>A vertical marker on the timeline: a release, or an incident opening.</summary>
public sealed record TimelineMarker
{
    /// <summary>"deployment" or "incident" - the UI renders them differently.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("at")]
    public required DateTimeOffset At { get; init; }

    /// <summary>Short label for the marker itself, e.g. "v2.14" or "INC-2391".</summary>
    [JsonPropertyName("label")]
    public required string Label { get; init; }

    [JsonPropertyName("service")]
    public required string Service { get; init; }

    /// <summary>Incident id, so a marker can be clicked through to the incident.</summary>
    [JsonPropertyName("incidentId")]
    public Guid? IncidentId { get; init; }

    /// <summary>Severity for incident markers, so colour matches the rest of the product.</summary>
    [JsonPropertyName("severity")]
    public string? Severity { get; init; }
}

public sealed record OverviewResponse
{
    [JsonPropertyName("windowStart")]
    public required DateTimeOffset WindowStart { get; init; }

    [JsonPropertyName("windowEnd")]
    public required DateTimeOffset WindowEnd { get; init; }

    /// <summary>Bucket width in minutes, so the chart can label its axis correctly.</summary>
    [JsonPropertyName("bucketMinutes")]
    public required int BucketMinutes { get; init; }

    [JsonPropertyName("activeIncidents")]
    public required MetricSummary ActiveIncidents { get; init; }

    [JsonPropertyName("errorEvents")]
    public required MetricSummary ErrorEvents { get; init; }

    [JsonPropertyName("servicesAffected")]
    public required MetricSummary ServicesAffected { get; init; }

    /// <summary>Mean time to resolution in minutes, over incidents resolved in the window.</summary>
    [JsonPropertyName("meanTimeToResolutionMinutes")]
    public required MetricSummary MeanTimeToResolutionMinutes { get; init; }

    [JsonPropertyName("aiInvestigations")]
    public required MetricSummary AiInvestigations { get; init; }

    /// <summary>Total services being monitored, for "3 of 5 affected".</summary>
    [JsonPropertyName("totalServices")]
    public required int TotalServices { get; init; }

    [JsonPropertyName("timeline")]
    public IReadOnlyList<TimelinePoint> Timeline { get; init; } = [];

    [JsonPropertyName("markers")]
    public IReadOnlyList<TimelineMarker> Markers { get; init; } = [];
}

/// <summary>
/// A row in the service health table.
///
/// Note what is absent: requests per minute and p95 latency. IncidentIQ
/// ingests logs, not request metrics - there is no counter and no latency
/// histogram anywhere in the system, so those numbers cannot be produced
/// honestly. Adding them needs a metrics pipeline, which is a product
/// decision rather than a missing endpoint.
/// </summary>
public sealed record ServiceHealth
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("ownerTeam")]
    public string? OwnerTeam { get; init; }

    /// <summary>Healthy, Degraded or Critical, derived from active incident severity.</summary>
    [JsonPropertyName("health")]
    public required string Health { get; init; }

    [JsonPropertyName("activeIncidents")]
    public required int ActiveIncidents { get; init; }

    [JsonPropertyName("errorEvents")]
    public required long ErrorEvents { get; init; }

    [JsonPropertyName("distinctErrorPatterns")]
    public required int DistinctErrorPatterns { get; init; }

    [JsonPropertyName("lastIncidentAt")]
    public DateTimeOffset? LastIncidentAt { get; init; }

    [JsonPropertyName("errorSeries")]
    public IReadOnlyList<double> ErrorSeries { get; init; } = [];
}
