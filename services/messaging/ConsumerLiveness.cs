using System.Collections.Concurrent;

namespace IncidentIQ.Messaging;

/// <summary>
/// What one consumer last reported about itself.
/// </summary>
/// <param name="Topic">The topic it subscribes to.</param>
/// <param name="ConsumerGroup">The group it joined.</param>
/// <param name="LastPollAt">
/// When the consume loop last completed an iteration. This advances on every
/// pass, including idle ones, so it stops advancing only if the loop has died,
/// hung, or is stuck inside a single message.
/// </param>
/// <param name="PartitionCount">
/// Partitions currently assigned. Zero is the signal that matters most: it is
/// what a consumer looks like after it has silently fallen out of its group.
/// </param>
/// <param name="AssignmentChangedAt">
/// When the partition count last changed. Used to tolerate a rebalance, which
/// legitimately holds zero partitions for a few seconds.
/// </param>
public sealed record ConsumerStatus(
    string Topic,
    string ConsumerGroup,
    DateTimeOffset LastPollAt,
    int PartitionCount,
    DateTimeOffset AssignmentChangedAt);

/// <summary>
/// Where the consume loops report that they are still alive, and where the
/// readiness probe reads it back.
///
/// This exists because of a real and repeatedly observed failure: a Kafka
/// consumer stops consuming while its process stays perfectly healthy. It was
/// seen twice, in two services and two languages - a consumer dropped out of
/// its group after a session timeout and never rejoined, and in one case a
/// second consumer in the *same container* kept working, so nothing about the
/// process looked wrong. The group had no members, lag sat unchanged, and
/// <c>/health/ready</c> stayed green throughout.
///
/// The existing Kafka health check does not catch this, and cannot: it asks
/// whether a broker answers. A broker that is perfectly reachable is exactly
/// the situation here. Reachability is a property of the cluster; consuming is
/// a property of this process, and only this process can report it.
///
/// For an incident-detection platform that failure is the worst shape a bug can
/// take - it stops detecting incidents and continues to claim it is fine.
/// </summary>
public sealed class ConsumerLivenessRegistry(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, ConsumerStatus> _consumers = new(StringComparer.Ordinal);

    /// <summary>
    /// How long a consumer may go without completing a loop iteration before it
    /// is considered stuck.
    ///
    /// Defaults to Kafka's own max poll interval, and deliberately so: that is
    /// the point at which the broker itself concludes the consumer is dead and
    /// rebalances its partitions away. Reporting unready sooner would call a
    /// slow handler a failure; reporting later would leave the service claiming
    /// health after Kafka had already given up on it.
    /// </summary>
    public TimeSpan PollTimeout { get; private set; } = TimeSpan.FromMilliseconds(300_000);

    /// <summary>
    /// Widens the poll timeout, never narrows it.
    ///
    /// One registry serves every consumer in the process, and they need not
    /// agree: the event-processor runs two. Taking the largest means a consumer
    /// is never failed while Kafka itself would still be waiting for it, which
    /// is the direction that cannot cause a false restart.
    /// </summary>
    public void AllowPollInterval(TimeSpan interval)
    {
        if (interval > PollTimeout)
        {
            PollTimeout = interval;
        }
    }

    /// <summary>
    /// How long a consumer may hold no partitions before that is a fault
    /// rather than a rebalance.
    ///
    /// A rebalance revokes everything for a few seconds, so this cannot be
    /// aggressive without restarting healthy containers during a deployment.
    /// </summary>
    public TimeSpan EmptyAssignmentGrace { get; set; } = TimeSpan.FromSeconds(90);

    private static string Key(string topic, string consumerGroup) => $"{consumerGroup}:{topic}";

    /// <summary>Called once when a consume loop starts, before its first poll.</summary>
    public void Register(string topic, string consumerGroup)
    {
        var now = timeProvider.GetUtcNow();

        _consumers[Key(topic, consumerGroup)] =
            new ConsumerStatus(topic, consumerGroup, now, PartitionCount: 0, AssignmentChangedAt: now);
    }

    /// <summary>
    /// Called on every pass of the consume loop, idle or not.
    ///
    /// Cheap by design - a dictionary write per iteration, several times a
    /// second - because anything the loop can skip under load is exactly the
    /// thing that will be missing when the loop is in trouble.
    /// </summary>
    public void ReportPoll(string topic, string consumerGroup, int partitionCount)
    {
        var now = timeProvider.GetUtcNow();
        var key = Key(topic, consumerGroup);

        _consumers.AddOrUpdate(
            key,
            _ => new ConsumerStatus(topic, consumerGroup, now, partitionCount, now),
            (_, existing) => existing with
            {
                LastPollAt = now,
                PartitionCount = partitionCount,
                // Only moved when the count actually changes, so the grace
                // period measures how long the assignment has been empty rather
                // than how long ago the last poll was.
                AssignmentChangedAt = existing.PartitionCount == partitionCount
                    ? existing.AssignmentChangedAt
                    : now
            });
    }

    /// <summary>Called on clean shutdown, so a stopping consumer is not reported as stuck.</summary>
    public void Deregister(string topic, string consumerGroup) =>
        _consumers.TryRemove(Key(topic, consumerGroup), out _);

    public IReadOnlyCollection<ConsumerStatus> Snapshot() => _consumers.Values.ToArray();

    /// <summary>
    /// The consumers that are not doing their job, and why, in words a person
    /// reading a failing probe can act on.
    /// </summary>
    public IReadOnlyList<string> Faults()
    {
        var now = timeProvider.GetUtcNow();
        var faults = new List<string>();

        foreach (var consumer in _consumers.Values)
        {
            var sincePoll = now - consumer.LastPollAt;

            if (sincePoll > PollTimeout)
            {
                faults.Add(
                    $"{consumer.ConsumerGroup} has not polled {consumer.Topic} for {sincePoll.TotalSeconds:F0}s "
                    + $"(limit {PollTimeout.TotalSeconds:F0}s).");
                continue;
            }

            // Reported separately from a stalled loop: a consumer that is
            // polling briskly and holds nothing has fallen out of its group,
            // which looks entirely different in the logs.
            var sinceChange = now - consumer.AssignmentChangedAt;

            if (consumer.PartitionCount == 0 && sinceChange > EmptyAssignmentGrace)
            {
                faults.Add(
                    $"{consumer.ConsumerGroup} has held no partitions of {consumer.Topic} for "
                    + $"{sinceChange.TotalSeconds:F0}s; it is polling but not a member of its group.");
            }
        }

        return faults;
    }
}
