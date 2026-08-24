using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace IncidentIQ.Shared.HealthChecks;

/// <summary>
/// Readiness probe for PostgreSQL. Opens a pooled connection and runs "SELECT 1".
/// Registered only when a connection string is actually configured, so a service
/// that does not use the database never reports a false negative.
/// </summary>
internal sealed class PostgresHealthCheck(string connectionString) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand("SELECT 1", connection);
            await command.ExecuteScalarAsync(cancellationToken);

            return HealthCheckResult.Healthy("PostgreSQL reachable.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL unreachable.", ex);
        }
    }
}
