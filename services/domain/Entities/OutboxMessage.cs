using IncidentIQ.Domain.Abstractions;

namespace IncidentIQ.Domain.Entities;

/// <summary>
/// A message waiting to be published to Kafka.
///
/// Solves the dual-write problem: a database commit and a Kafka publish cannot
/// be made atomic, so the publish becomes part of the transaction. The domain
/// row and its outbox row commit together, and a separate poller forwards the
/// row to Kafka afterwards. If the process dies at any point, either both rows
/// exist or neither does - never an incident that no consumer will ever hear
/// about, and never an event announcing an incident that was rolled back.
///
/// The key is a bigint rather than a UUID because the poller reads this table
/// in creation order; a monotonic sequence is exactly the ordering it wants.
/// </summary>
public class OutboxMessage : ITenantScoped, ICreatedAt
{
    public long Id { get; set; }

    public Guid OrganizationId { get; set; }

    /// <summary>
    /// The envelope's event id, fixed when the row is written rather than when
    /// it is published.
    ///
    /// This is what makes retrying safe. A publish that succeeded but crashed
    /// before the row could be marked published will be retried, and the
    /// consumer has to be able to recognise the second copy as the same event.
    /// Generating the id at publish time would produce two events that no
    /// consumer could tell apart, defeating every downstream idempotency
    /// mechanism at once.
    /// </summary>
    public Guid EventId { get; set; }

    /// <summary>Ties this event to the original action that caused it.</summary>
    public Guid CorrelationId { get; set; }

    /// <summary>e.g. "Incident".</summary>
    public string AggregateType { get; set; } = null!;

    /// <summary>The aggregate this event is about.</summary>
    public Guid AggregateId { get; set; }

    /// <summary>e.g. "incident.detected".</summary>
    public string EventType { get; set; } = null!;

    public int EventVersion { get; set; } = 1;

    /// <summary>Destination topic. Stored rather than derived, so a later routing change does not rewrite queued history.</summary>
    public string Topic { get; set; } = null!;

    /// <summary>
    /// The exact Kafka partition key, computed when the row was written.
    ///
    /// Stored verbatim rather than rebuilt from AggregateId at publish time:
    /// the key format is a decision of the code that raised the event, and
    /// changing that format later must not silently re-route messages that are
    /// already queued.
    /// </summary>
    public string PartitionKey { get; set; } = null!;

    /// <summary>
    /// The complete serialised envelope, ready to send.
    ///
    /// Not just the payload, and stored as text rather than jsonb. jsonb parses
    /// and re-serialises on the way in, so it changes whitespace and key order -
    /// the stored value would no longer be the bytes that were committed. Text
    /// keeps a republish byte-identical to the first attempt, which is what
    /// makes it safe to claim the two are the same event, and leaves the door
    /// open to checksumming or signing the payload later.
    ///
    /// Nothing is lost by it: every field worth querying - event id, type,
    /// correlation id, topic, partition key - is already a column.
    /// </summary>
    public string Payload { get; set; } = null!;

    /// <summary>Kafka headers as jsonb.</summary>
    public string? Headers { get; set; }

    /// <summary>When the domain event happened, as opposed to when it was published.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Null while pending. The publisher's partial index covers exactly these rows.</summary>
    public DateTimeOffset? PublishedAt { get; set; }

    public int AttemptCount { get; set; }

    /// <summary>
    /// Earliest time the next attempt may run. Without it, a broker outage
    /// becomes a tight retry loop that saturates the database and the broker at
    /// the moment they can least afford it.
    /// </summary>
    public DateTimeOffset? NextAttemptAt { get; set; }

    public string? LastError { get; set; }

    /// <summary>
    /// Set once the attempt limit is reached. The row stops being retried and
    /// starts being an alert: something is wrong that retrying will not fix,
    /// and a human needs to look at it.
    /// </summary>
    public DateTimeOffset? DeadLetteredAt { get; set; }
}
