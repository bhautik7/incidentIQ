using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IncidentIQ.Messaging;

/// <summary>
/// Readiness for this process's consumers, as distinct from the broker's.
///
/// The existing Kafka check asks whether a broker answers. This one asks
/// whether we are actually consuming, which is a different question with a
/// different answer: every observed instance of this failure had a perfectly
/// reachable broker and a consumer that had quietly stopped.
///
/// Readiness rather than liveness, deliberately. Unready pulls the container
/// out of rotation and shows up in the probe output with the reason; unhealthy
/// liveness would restart it, and restarting on a rebalance - which briefly
/// looks identical - would turn one slow deployment into a loop.
/// </summary>
public sealed class KafkaConsumerHealthCheck(ConsumerLivenessRegistry registry) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var consumers = registry.Snapshot();

        if (consumers.Count == 0)
        {
            // Nothing has registered yet. During startup that is ordinary, and
            // claiming a fault would make every container flap on boot.
            return Task.FromResult(HealthCheckResult.Healthy("No consumers registered."));
        }

        var faults = registry.Faults();

        var data = consumers.ToDictionary(
            consumer => $"{consumer.ConsumerGroup}:{consumer.Topic}",
            object (consumer) => new
            {
                partitions = consumer.PartitionCount,
                lastPollAt = consumer.LastPollAt
            });

        if (faults.Count > 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(string.Join(" ", faults), data: data));
        }

        var partitions = consumers.Sum(consumer => consumer.PartitionCount);

        return Task.FromResult(HealthCheckResult.Healthy(
            $"{consumers.Count} consumer(s) polling, {partitions} partition(s) assigned.", data));
    }
}
