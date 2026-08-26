using System.Text.Json.Serialization;

namespace IncidentIQ.Api.Contracts;

/// <summary>
/// One page of results, plus what the caller needs to ask for the next one.
///
/// Every list endpoint returns this shape, so a client writes pagination once.
/// </summary>
public sealed record PagedResult<T>
{
    [JsonPropertyName("items")]
    public required IReadOnlyList<T> Items { get; init; }

    [JsonPropertyName("page")]
    public required int Page { get; init; }

    [JsonPropertyName("pageSize")]
    public required int PageSize { get; init; }

    [JsonPropertyName("totalCount")]
    public required int TotalCount { get; init; }

    [JsonPropertyName("totalPages")]
    public int TotalPages => PageSize == 0 ? 0 : (int)Math.Ceiling(TotalCount / (double)PageSize);
}

/// <summary>
/// A row in the incident list.
///
/// Deliberately narrow: everything here is needed to render a list item and
/// decide what to click. The expensive parts - the timeline, the raw samples,
/// the full analysis - are fetched only when an incident is actually opened,
/// so a list of 50 does not cost 50 analyses.
/// </summary>
public sealed record IncidentListItem
{
    [JsonPropertyName("id")]
    public required Guid Id { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("detectionRule")]
    public required string DetectionRule { get; init; }

    [JsonPropertyName("service")]
    public required string Service { get; init; }

    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    [JsonPropertyName("occurrenceCount")]
    public required long OccurrenceCount { get; init; }

    [JsonPropertyName("firstSeenAt")]
    public required DateTimeOffset FirstSeenAt { get; init; }

    [JsonPropertyName("lastSeenAt")]
    public required DateTimeOffset LastSeenAt { get; init; }

    /// <summary>The release the detector suspected, if any. The single most useful column in a list.</summary>
    [JsonPropertyName("suspectedDeploymentVersion")]
    public string? SuspectedDeploymentVersion { get; init; }

    /// <summary>Lets the UI show "analysing…" rather than an empty space.</summary>
    [JsonPropertyName("hasAnalysis")]
    public required bool HasAnalysis { get; init; }

    [JsonPropertyName("analysisConfidence")]
    public decimal? AnalysisConfidence { get; init; }
}

public sealed record IncidentPattern
{
    [JsonPropertyName("fingerprint")]
    public required string Fingerprint { get; init; }

    /// <summary>The masked template - what makes many lines one pattern.</summary>
    [JsonPropertyName("messageTemplate")]
    public required string MessageTemplate { get; init; }

    /// <summary>One real, unmasked message. Safe here: this is the tenant's own data, shown to their own user.</summary>
    [JsonPropertyName("sampleMessage")]
    public required string SampleMessage { get; init; }

    [JsonPropertyName("exceptionType")]
    public string? ExceptionType { get; init; }

    [JsonPropertyName("httpStatusCode")]
    public int? HttpStatusCode { get; init; }

    [JsonPropertyName("occurrenceCount")]
    public required long OccurrenceCount { get; init; }
}

public sealed record IncidentDeployment
{
    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("deployedAt")]
    public required DateTimeOffset DeployedAt { get; init; }

    [JsonPropertyName("commitSha")]
    public string? CommitSha { get; init; }

    [JsonPropertyName("deployedBy")]
    public string? DeployedBy { get; init; }

    /// <summary>Negative would mean the release came after; the useful direction is positive.</summary>
    [JsonPropertyName("minutesBeforeIncident")]
    public required double MinutesBeforeIncident { get; init; }
}

public sealed record IncidentAnalysis
{
    /// <summary>"anthropic" when a model wrote it, "deterministic" when it is templated.</summary>
    [JsonPropertyName("modelProvider")]
    public required string ModelProvider { get; init; }

    [JsonPropertyName("modelName")]
    public string? ModelName { get; init; }

    [JsonPropertyName("confidence")]
    public decimal? Confidence { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("probableCause")]
    public string? ProbableCause { get; init; }

    [JsonPropertyName("suggestedActions")]
    public IReadOnlyList<string> SuggestedActions { get; init; } = [];

    [JsonPropertyName("similarIncidents")]
    public IReadOnlyList<SimilarIncident> SimilarIncidents { get; init; } = [];

    [JsonPropertyName("createdAt")]
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record SimilarIncident
{
    [JsonPropertyName("incidentId")]
    public required string IncidentId { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("similarity")]
    public required double Similarity { get; init; }

    [JsonPropertyName("resolutionNotes")]
    public string? ResolutionNotes { get; init; }
}

public sealed record IncidentTimelineEntry
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("occurredAt")]
    public required DateTimeOffset OccurredAt { get; init; }

    [JsonPropertyName("actorType")]
    public required string ActorType { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

public sealed record IncidentSample
{
    [JsonPropertyName("occurredAt")]
    public required DateTimeOffset OccurredAt { get; init; }

    [JsonPropertyName("level")]
    public required string Level { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("host")]
    public string? Host { get; init; }

    [JsonPropertyName("traceId")]
    public string? TraceId { get; init; }
}

/// <summary>Everything one incident page needs, in one round trip.</summary>
public sealed record IncidentDetail
{
    [JsonPropertyName("incident")]
    public required IncidentListItem Incident { get; init; }

    [JsonPropertyName("pattern")]
    public IncidentPattern? Pattern { get; init; }

    [JsonPropertyName("deployment")]
    public IncidentDeployment? Deployment { get; init; }

    [JsonPropertyName("analysis")]
    public IncidentAnalysis? Analysis { get; init; }

    [JsonPropertyName("timeline")]
    public IReadOnlyList<IncidentTimelineEntry> Timeline { get; init; } = [];

    /// <summary>A handful of real log lines. Capped - log_events is a sample, not an archive.</summary>
    [JsonPropertyName("samples")]
    public IReadOnlyList<IncidentSample> Samples { get; init; } = [];
}

/// <summary>Counts for the header. One query, so the list page costs two round trips total.</summary>
public sealed record IncidentStats
{
    [JsonPropertyName("detected")]
    public required int Detected { get; init; }

    [JsonPropertyName("investigating")]
    public required int Investigating { get; init; }

    [JsonPropertyName("resolvedLast24Hours")]
    public required int ResolvedLast24Hours { get; init; }

    [JsonPropertyName("critical")]
    public required int Critical { get; init; }

    [JsonPropertyName("totalOccurrences")]
    public required long TotalOccurrences { get; init; }
}

public sealed record ServiceSummary
{
    [JsonPropertyName("key")]
    public required string Key { get; init; }

    [JsonPropertyName("displayName")]
    public required string DisplayName { get; init; }

    [JsonPropertyName("activeIncidents")]
    public required int ActiveIncidents { get; init; }
}
