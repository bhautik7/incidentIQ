using System.Text.Json.Serialization;

namespace IncidentIQ.Contracts.Payloads;

/// <summary>
/// A release went out. Travels on <c>deployments.created</c>.
///
/// Low volume and high value: most incidents start minutes after a deployment,
/// so this is what lets the processor answer "what shipped just before this?"
/// without querying a CI system during an outage.
/// </summary>
public sealed record DeploymentCreated
{
    [JsonPropertyName("deploymentId")]
    public required Guid DeploymentId { get; init; }

    [JsonPropertyName("service")]
    public required string Service { get; init; }

    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    [JsonPropertyName("version")]
    public required string Version { get; init; }

    [JsonPropertyName("deployedAt")]
    public required DateTimeOffset DeployedAt { get; init; }

    [JsonPropertyName("commitSha")]
    public string? CommitSha { get; init; }

    [JsonPropertyName("deployedBy")]
    public string? DeployedBy { get; init; }
}

/// <summary>
/// A new incident was opened. Travels on <c>incidents.detected</c>, published
/// through the transactional outbox rather than directly - the incident row and
/// this event commit together, so an incident can never exist that no consumer
/// hears about.
///
/// Deliberately a thin pointer rather than a full incident. Consumers read the
/// current state from PostgreSQL, which means adding a field to the incident
/// does not require a schema version bump here, and a consumer that runs
/// minutes late sees current data rather than a stale snapshot.
/// </summary>
public sealed record IncidentDetected
{
    [JsonPropertyName("incidentId")]
    public required Guid IncidentId { get; init; }

    [JsonPropertyName("logPatternId")]
    public required Guid LogPatternId { get; init; }

    [JsonPropertyName("service")]
    public required string Service { get; init; }

    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("firstSeenAt")]
    public required DateTimeOffset FirstSeenAt { get; init; }
}

/// <summary>
/// Work request for the Python AI worker. Travels on
/// <c>incidents.analysis.requested</c>.
///
/// Separate from <see cref="IncidentDetected"/> because analysis is also
/// requested for incidents that were detected long ago - when a prompt changes,
/// when a model is upgraded, or when a failed analysis is retried. Overloading
/// the detection event would make "re-analyse" indistinguishable from
/// "this just happened".
/// </summary>
public sealed record IncidentAnalysisRequested
{
    [JsonPropertyName("incidentId")]
    public required Guid IncidentId { get; init; }

    /// <summary>
    /// Which analysis this is. Combined with the incident id it forms the
    /// idempotency key, so a redelivered request writes nothing.
    /// </summary>
    [JsonPropertyName("analysisVersion")]
    public required int AnalysisVersion { get; init; }

    /// <summary>Why the analysis was asked for: "detected", "prompt-changed", "retry".</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("requestedAt")]
    public required DateTimeOffset RequestedAt { get; init; }
}

/// <summary>
/// Result written back by the Python AI worker. Travels on
/// <c>incidents.analysis.completed</c>.
///
/// The worker writes the full analysis - including the embedding - straight to
/// PostgreSQL. This event only announces that it did, so anything that reacts
/// (a dashboard push, a notification) does not have to poll. Embeddings are
/// 1,536 floats and have no business on a Kafka topic.
/// </summary>
public sealed record IncidentAnalysisCompleted
{
    [JsonPropertyName("incidentId")]
    public required Guid IncidentId { get; init; }

    [JsonPropertyName("analysisId")]
    public required Guid AnalysisId { get; init; }

    [JsonPropertyName("analysisVersion")]
    public required int AnalysisVersion { get; init; }

    /// <summary>"Completed" or "Failed". A failed analysis is still an outcome worth announcing.</summary>
    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("modelName")]
    public string? ModelName { get; init; }

    [JsonPropertyName("confidence")]
    public decimal? Confidence { get; init; }

    [JsonPropertyName("similarIncidentCount")]
    public int SimilarIncidentCount { get; init; }

    [JsonPropertyName("completedAt")]
    public required DateTimeOffset CompletedAt { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }
}
