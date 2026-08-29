using System.Text.Json.Serialization;

namespace IncidentIQ.Api.Contracts;

/// <summary>
/// "We just shipped this." Sent by CI, or by a person after a manual release.
///
/// The smallest thing a deployment can be and still be useful: what shipped,
/// where, and when. Everything else on the row is optional, because a release
/// notification that is refused for missing a commit SHA is a release
/// notification that stops being sent.
/// </summary>
public sealed record RecordDeploymentRequest
{
    [JsonPropertyName("service")]
    public string? Service { get; init; }

    [JsonPropertyName("environment")]
    public string? Environment { get; init; }

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>
    /// When it went out. Defaults to now, which is right for a CI step that
    /// runs at the end of a deploy and wrong for a backfill, so it is accepted
    /// explicitly as well.
    /// </summary>
    [JsonPropertyName("deployedAt")]
    public DateTimeOffset? DeployedAt { get; init; }

    [JsonPropertyName("commitSha")]
    public string? CommitSha { get; init; }

    [JsonPropertyName("deployedBy")]
    public string? DeployedBy { get; init; }

    /// <summary>InProgress, Succeeded, Failed or RolledBack. Defaults to Succeeded.</summary>
    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

public sealed record RecordDeploymentResult
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

    /// <summary>
    /// Incidents already open for this service and environment whose first
    /// occurrence falls after this release.
    ///
    /// Returned because a deploy step that gets back "this release is now
    /// suspected in 2 open incidents" has learned something worth acting on
    /// before the pipeline moves to the next stage.
    /// </summary>
    [JsonPropertyName("correlatedIncidentIds")]
    public IReadOnlyList<Guid> CorrelatedIncidentIds { get; init; } = [];
}
