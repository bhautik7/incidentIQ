using Confluent.Kafka;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IncidentIQ.Shared.HealthChecks;

/// <summary>
/// Readiness probe for Kafka. Asks the cluster for metadata, which proves both
/// DNS resolution and that a broker is accepting connections.
/// The admin client is created once and reused: rebuilding it on every probe
/// would open a new TCP connection every few seconds.
/// </summary>
internal sealed class KafkaHealthCheck(string bootstrapServers) : IHealthCheck, IDisposable
{
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(5);

    private readonly Lazy<IAdminClient> _adminClient = new(() =>
        new AdminClientBuilder(new AdminClientConfig
        {
            BootstrapServers = bootstrapServers,
            SocketTimeoutMs = 5_000,
            LogConnectionClose = false
        }).Build());

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var metadata = _adminClient.Value.GetMetadata(MetadataTimeout);
            return Task.FromResult(HealthCheckResult.Healthy(
                $"Kafka reachable ({metadata.Brokers.Count} broker(s), {metadata.Topics.Count} topic(s))."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("Kafka unreachable.", ex));
        }
    }

    public void Dispose()
    {
        if (_adminClient.IsValueCreated)
        {
            _adminClient.Value.Dispose();
        }
    }
}
