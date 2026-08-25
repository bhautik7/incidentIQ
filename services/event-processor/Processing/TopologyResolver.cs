using System.Collections.Concurrent;
using IncidentIQ.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace IncidentIQ.EventProcessor.Processing;

/// <summary>
/// Turns the names a client uses - "payments-api", "production" - into the
/// database ids the schema needs, creating rows for names never seen before.
///
/// This is the lookup the whole pipeline hits hardest: once per log line, for
/// a handful of rows that almost never change. It is cached in-process for
/// exactly that reason. The cache is unbounded only in the sense that an
/// organization's services and environments are - tens of entries, not
/// millions - and entries are never invalidated because ids never change.
///
/// This is the workload ADR 0006 named as the trigger for adopting Redis. It
/// is not the trigger yet: a process-local dictionary answers it in
/// nanoseconds and survives a database outage, and a shared cache would only
/// start paying for itself across many replicas.
/// </summary>
public sealed class TopologyResolver(IncidentIQDbContext dbContext, ILogger<TopologyResolver> logger)
{
    private static readonly ConcurrentDictionary<(Guid Tenant, string Kind, string Key), Guid> Cache = new();
    private static readonly ConcurrentDictionary<Guid, bool> KnownOrganizations = new();

    /// <summary>
    /// True when the organization exists. An event for an unknown tenant cannot
    /// be persisted - every foreign key would fail - and no retry will make the
    /// organization appear, so callers treat this as permanent.
    /// </summary>
    public async Task<bool> OrganizationExistsAsync(Guid organizationId, CancellationToken cancellationToken)
    {
        if (KnownOrganizations.ContainsKey(organizationId))
        {
            return true;
        }

        var exists = await dbContext.Organizations
            .IgnoreQueryFilters()
            .AnyAsync(o => o.Id == organizationId, cancellationToken);

        if (exists)
        {
            KnownOrganizations[organizationId] = true;
        }

        return exists;
    }

    public Task<Guid> ResolveServiceIdAsync(Guid organizationId, string key, CancellationToken cancellationToken) =>
        ResolveAsync(organizationId, "service", key, cancellationToken);

    public Task<Guid> ResolveEnvironmentIdAsync(Guid organizationId, string key, CancellationToken cancellationToken) =>
        ResolveAsync(organizationId, "environment", key, cancellationToken);

    private async Task<Guid> ResolveAsync(Guid organizationId, string kind, string key, CancellationToken cancellationToken)
    {
        var normalized = key.Trim().ToLowerInvariant();
        var cacheKey = (organizationId, kind, normalized);

        if (Cache.TryGetValue(cacheKey, out var cached))
        {
            return cached;
        }

        var id = kind == "service"
            ? await UpsertServiceAsync(organizationId, normalized, cancellationToken)
            : await UpsertEnvironmentAsync(organizationId, normalized, cancellationToken);

        Cache[cacheKey] = id;
        return id;
    }

    /// <summary>
    /// Insert-if-absent then read, in one statement.
    ///
    /// Written as raw SQL because the race matters: two processor replicas can
    /// see the same new service name in the same millisecond. ON CONFLICT makes
    /// the loser of that race read the winner's row instead of failing, which a
    /// read-then-write in EF cannot do.
    /// </summary>
    private async Task<Guid> UpsertServiceAsync(Guid organizationId, string key, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH inserted AS (
                INSERT INTO monitored_services
                    (id, organization_id, key, display_name, is_active, created_at, updated_at)
                VALUES (@id, @org, @key, @display, true, now(), now())
                ON CONFLICT (organization_id, key) DO NOTHING
                RETURNING id
            )
            SELECT id FROM inserted
            UNION ALL
            SELECT id FROM monitored_services WHERE organization_id = @org AND key = @key
            LIMIT 1;
            """;

        var id = await ScalarGuidAsync(sql, organizationId, key, cancellationToken);

        logger.LogDebug("Resolved service {Key} for {OrganizationId} to {Id}", key, organizationId, id);
        return id;
    }

    private async Task<Guid> UpsertEnvironmentAsync(Guid organizationId, string key, CancellationToken cancellationToken)
    {
        const string sql = """
            WITH inserted AS (
                INSERT INTO environments
                    (id, organization_id, key, display_name, rank, is_production, created_at, updated_at)
                VALUES (@id, @org, @key, @display, 0, @isProduction, now(), now())
                ON CONFLICT (organization_id, key) DO NOTHING
                RETURNING id
            )
            SELECT id FROM inserted
            UNION ALL
            SELECT id FROM environments WHERE organization_id = @org AND key = @key
            LIMIT 1;
            """;

        var id = await ScalarGuidAsync(sql, organizationId, key, cancellationToken,
            isProduction: key is "production" or "prod");

        logger.LogDebug("Resolved environment {Key} for {OrganizationId} to {Id}", key, organizationId, id);
        return id;
    }

    private async Task<Guid> ScalarGuidAsync(
        string sql,
        Guid organizationId,
        string key,
        CancellationToken cancellationToken,
        bool? isProduction = null)
    {
        var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();

        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", Guid.CreateVersion7());
        command.Parameters.AddWithValue("org", organizationId);
        command.Parameters.AddWithValue("key", key);
        command.Parameters.AddWithValue("display", key);

        if (isProduction is not null)
        {
            command.Parameters.AddWithValue("isProduction", isProduction.Value);
        }

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return (Guid)result!;
    }

    /// <summary>Test hook; the cache is process-wide and would otherwise leak across cases.</summary>
    internal static void ClearCache()
    {
        Cache.Clear();
        KnownOrganizations.Clear();
    }
}
