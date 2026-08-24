using IncidentIQ.Domain.Abstractions;
using IncidentIQ.Domain.Enums;
using Pgvector;

namespace IncidentIQ.Domain.Entities;

/// <summary>
/// The machine-generated explanation of an <see cref="Incident"/>: an
/// embedding, the similar incidents it found, and the LLM's summary.
///
/// Kept in its own table rather than as columns on Incident for three reasons.
/// It is written by a different process (the Python worker) on a different
/// schedule; it is *disposable* - re-runnable when a prompt or model changes,
/// which is what <see cref="AnalysisVersion"/> tracks; and an incident with no
/// analysis yet, or a failed one, is a normal state rather than a half-filled
/// row.
///
/// The unique constraint on (IncidentId, AnalysisVersion) is the idempotency
/// key: a redelivered Kafka message re-runs the analysis and writes nothing.
/// </summary>
public class AiAnalysis : ITenantScoped, ICreatedAt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid IncidentId { get; set; }

    /// <summary>Bumped when the prompt or model changes, so old results stay comparable.</summary>
    public int AnalysisVersion { get; set; } = 1;

    public AiAnalysisStatus Status { get; set; } = AiAnalysisStatus.Pending;

    /// <summary>
    /// Embedding of the incident signature. This is why the platform runs
    /// pgvector: "has this happened before?" is a nearest-neighbour query that
    /// exact text matching cannot answer, because the same failure is described
    /// differently by different libraries.
    /// </summary>
    public Vector? Embedding { get; set; }

    public string? EmbeddingModel { get; set; }
    public string? ModelProvider { get; set; }
    public string? ModelName { get; set; }

    public string? Summary { get; set; }
    public string? ProbableCause { get; set; }

    /// <summary>jsonb array of suggested checks, in priority order.</summary>
    public string? SuggestedActions { get; set; }

    /// <summary>jsonb array of {incidentId, similarity, title} from the vector search.</summary>
    public string? SimilarIncidents { get; set; }

    /// <summary>0.000-1.000. Low confidence is shown to the user, not hidden.</summary>
    public decimal? Confidence { get; set; }

    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? LatencyMs { get; set; }

    /// <summary>Populated when Status is Failed, so a retry has something to act on.</summary>
    public string? Error { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public Incident Incident { get; set; } = null!;
}
