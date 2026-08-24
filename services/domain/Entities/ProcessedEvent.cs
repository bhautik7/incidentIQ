namespace IncidentIQ.Domain.Entities;

/// <summary>
/// The inbox side of exactly-once processing: a record that a given consumer
/// group has already handled a given message.
///
/// Kafka delivers at least once, so every consumer will see duplicates - after
/// a rebalance, after a crash between the database commit and the offset
/// commit, and after a deliberate dead-letter replay. Where a natural key
/// exists the unique constraint on the target table is the better defence
/// (LogEvents.EventId, AiAnalyses.AnalysisVersion). This table covers the rest:
/// handlers whose effect is not a single insert.
///
/// The primary key is (ConsumerGroup, EventId): the same message legitimately
/// gets processed once per consumer group, and only a repeat within one group
/// is a duplicate.
/// </summary>
public class ProcessedEvent
{
    /// <summary>e.g. "incident-processor".</summary>
    public string ConsumerGroup { get; set; } = null!;

    /// <summary>The message's idempotency key.</summary>
    public Guid EventId { get; set; }

    /// <summary>
    /// Nullable: some messages are handled before the owning organization is
    /// known. This is the one tenant-scoped-ish table where that is expected.
    /// </summary>
    public Guid? OrganizationId { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }

    /// <summary>Indexed so a retention job can delete rows older than the Kafka retention window.</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}
