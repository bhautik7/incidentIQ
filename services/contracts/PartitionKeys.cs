namespace IncidentIQ.Contracts;

/// <summary>
/// How a message is assigned to a partition.
///
/// This is the single most consequential Kafka decision in the system, because
/// the partition key determines both ordering and which consumer instance sees
/// a message - and correctness depends on both.
///
/// The rule: everything that must be processed together, and in order, must
/// share a key.
/// </summary>
public static class PartitionKeys
{
    /// <summary>
    /// Key for everything on the log path: <c>{tenantId}:{service}</c>.
    ///
    /// All log events for one service, in one organization, land on one
    /// partition and therefore reach one consumer instance in order. That is
    /// what makes incident correlation correct: two processor replicas can
    /// never race to open two incidents for the same fingerprint, because the
    /// same fingerprint always arrives at the same replica.
    ///
    /// The tenant is included even though a service id would be unique on its
    /// own, for two reasons. Ingestion works with the client's *name* for a
    /// service ("payments-api"), not a resolved id - names are only unique
    /// within an organization. And a key that reads
    /// "acme:payments-api" is one a human can act on when inspecting a lagging
    /// partition; a bare UUID is not.
    ///
    /// Known risk: one very noisy service pins one partition. That is accepted
    /// for now - Kafka absorbs the burst and the symptom is recoverable lag,
    /// not loss. The escape hatch, if a real service ever saturates a
    /// partition, is <see cref="ForShardedService"/>.
    /// </summary>
    public static string ForService(Guid tenantId, string service) => $"{tenantId:D}:{service}";

    /// <summary>
    /// Key for everything about one incident: <c>{tenantId}:{incidentId}</c>.
    ///
    /// Keeps an incident's lifecycle ordered - detected, then analysis
    /// requested, then analysis completed - so a late-delivered earlier event
    /// cannot overwrite a newer result. Incident ids are high-cardinality, so
    /// this also distributes evenly across partitions with no effort.
    /// </summary>
    public static string ForIncident(Guid tenantId, Guid incidentId) => $"{tenantId:D}:{incidentId:D}";

    /// <summary>
    /// Escape hatch for a single service so noisy it saturates one partition:
    /// spread it over N sub-keys while keeping each fingerprint on one of them.
    ///
    /// Deriving the shard from the fingerprint rather than at random is what
    /// preserves correctness - all occurrences of one error still meet on one
    /// partition, they are simply no longer all on the *same* partition as the
    /// rest of that service's traffic.
    ///
    /// Not used anywhere yet. Do not reach for it before a partition has
    /// actually been measured as the bottleneck.
    /// </summary>
    public static string ForShardedService(Guid tenantId, string service, string fingerprint, int shardCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(shardCount, 1);

        var shard = (uint)fingerprint.GetHashCode(StringComparison.Ordinal) % (uint)shardCount;
        return $"{tenantId:D}:{service}:{shard}";
    }
}
