using IncidentIQ.Domain.Abstractions;

namespace IncidentIQ.Domain.Entities;

/// <summary>
/// A message waiting to be published to Kafka.
///
/// Solves the dual-write problem: a database commit and a Kafka publish cannot
/// be made atomic, so the publish becomes part of the transaction. The incident
/// row and its outbox row commit together, and a separate poller forwards the
/// row to Kafka afterwards. If the process dies at any point, either both rows
/// exist or neither does - never an incident that no consumer will ever hear
/// about.
///
/// The key is a bigint rather than a UUID because the poller reads this table
/// in creation order; a monotonic sequence is exactly the ordering it wants.
/// </summary>
public class OutboxMessage : ITenantScoped, ICreatedAt
{
    public long Id { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>e.g. "Incident".</summary>
    public string AggregateType { get; set; } = null!;

    /// <summary>The aggregate's id, which becomes the Kafka partition key.</summary>
    public Guid AggregateId { get; set; }

    /// <summary>e.g. "IncidentCreated".</summary>
    public string EventType { get; set; } = null!;

    /// <summary>The event body as jsonb.</summary>
    public string Payload { get; set; } = null!;

    /// <summary>Kafka headers as jsonb: correlation id, schema version.</summary>
    public string? Headers { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Null while pending. The publisher's partial index covers exactly these rows.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
}
