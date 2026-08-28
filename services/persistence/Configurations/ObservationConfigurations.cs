using IncidentIQ.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Environment = IncidentIQ.Domain.Entities.Environment;

namespace IncidentIQ.Persistence.Configurations;

public class LogPatternConfiguration : IEntityTypeConfiguration<LogPattern>
{
    public void Configure(EntityTypeBuilder<LogPattern> builder)
    {
        builder.ToTable("log_patterns");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Fingerprint).HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(x => x.Level).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.ExceptionType).HasMaxLength(500);
        builder.Property(x => x.MessageTemplate).HasMaxLength(4000).IsRequired();
        builder.Property(x => x.SampleMessage).HasMaxLength(4000).IsRequired();
        builder.Property(x => x.TopStackFrames).HasMaxLength(4000);
        builder.Property(x => x.OccurrenceCount).HasDefaultValue(0L);

        // The hot path: every incoming log event looks up its fingerprint here.
        // Unique per organization, so two tenants producing the identical error
        // get separate patterns.
        builder.HasIndex(x => new { x.OrganizationId, x.Fingerprint }).IsUnique();

        // The server-error spike rule's join: 5xx patterns for one service.
        builder.HasIndex(x => new { x.OrganizationId, x.MonitoredServiceId, x.EnvironmentId, x.HttpStatusCode })
            .HasFilter("http_status_code >= 500");

        // "Most recently active patterns for this service and environment."
        builder.HasIndex(x => new { x.OrganizationId, x.MonitoredServiceId, x.EnvironmentId, x.LastSeenAt })
            .IsDescending(false, false, false, true);

        builder.HasOne(x => x.MonitoredService)
            .WithMany(x => x.LogPatterns)
            .HasForeignKey(x => new { x.OrganizationId, x.MonitoredServiceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Environment)
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.EnvironmentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class RawLogEventConfiguration : IEntityTypeConfiguration<RawLogEvent>
{
    public void Configure(EntityTypeBuilder<RawLogEvent> builder)
    {
        builder.ToTable("raw_log_events");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityAlwaysColumn();

        builder.Property(x => x.Level).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Message).HasMaxLength(8000).IsRequired();
        builder.Property(x => x.ExceptionType).HasMaxLength(500);
        builder.Property(x => x.StackTrace).HasColumnType("text");
        builder.Property(x => x.TraceId).HasMaxLength(64);
        builder.Property(x => x.SpanId).HasMaxLength(32);
        builder.Property(x => x.Host).HasMaxLength(255);
        builder.Property(x => x.Properties).HasColumnType("jsonb");

        // ---- Indexes ----
        //
        // This is the only table that grows with traffic rather than with
        // distinct errors, so every index here is paid for on every single log
        // line. The set is the smallest one that serves the log explorer.

        // 1. The explorer's one access path: newest first, within a tenant.
        //    Id descends alongside occurred_at to make the order total, which
        //    is what lets the cursor be a keyset rather than an offset - two
        //    lines logged in the same millisecond must not be able to swap
        //    places between pages and hide a row.
        builder.HasIndex(x => new { x.OrganizationId, x.OccurredAt, x.Id })
            .IsDescending(false, true, true);

        // 2. Trace correlation. Partial, because most lines carry no trace id
        //    and indexing the nulls would roughly double the index for nothing.
        builder.HasIndex(x => new { x.OrganizationId, x.TraceId })
            .HasFilter("trace_id IS NOT NULL");

        // No index on level, and none on message. Both are low-cardinality or
        // unindexable-by-prefix, so the planner is better served filtering rows
        // it has already found by time than by an index maintained on every
        // insert. EF adds its own indexes for the foreign keys below; those are
        // kept because a cascade from a deleted service would otherwise scan
        // the largest table in the schema.
        //
        // Retention is served by a BRIN index on occurred_at, created in the
        // migration because EF cannot express one. It suits an append-only
        // table physically ordered by time and costs a rounding error per
        // insert, where a btree would cost more than the delete it exists for.

        builder.HasOne(x => x.MonitoredService)
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.MonitoredServiceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Environment)
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.EnvironmentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.LogPattern)
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.LogPatternId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class LogEventConfiguration : IEntityTypeConfiguration<LogEvent>
{
    public void Configure(EntityTypeBuilder<LogEvent> builder)
    {
        builder.ToTable("log_events");

        // bigint identity: monotonic, half the width of a UUID, and never
        // referenced from outside the database.
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityAlwaysColumn();

        builder.Property(x => x.Level).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Message).HasMaxLength(8000).IsRequired();
        builder.Property(x => x.ExceptionType).HasMaxLength(500);
        builder.Property(x => x.StackTrace).HasColumnType("text");
        builder.Property(x => x.TraceId).HasMaxLength(64);
        builder.Property(x => x.SpanId).HasMaxLength(32);
        builder.Property(x => x.Host).HasMaxLength(255);
        builder.Property(x => x.Properties).HasColumnType("jsonb");

        // ---- Indexes. Every one of these is a tax on every insert, so the set
        // is deliberately small and each entry has a named query behind it. ----

        // 1. Idempotency. Not optional: this is what makes redelivery a no-op.
        builder.HasIndex(x => new { x.OrganizationId, x.EventId }).IsUnique();

        // 2. Incident drill-down - the most-run query in the product.
        //    Partial, because most rows are never attached to an incident.
        builder.HasIndex(x => new { x.IncidentId, x.OccurredAt })
            .IsDescending(false, true)
            .HasFilter("incident_id IS NOT NULL");

        // 3. "Show me occurrences of this pattern over time."
        builder.HasIndex(x => new { x.OrganizationId, x.LogPatternId, x.OccurredAt })
            .IsDescending(false, false, true)
            .HasFilter("log_pattern_id IS NOT NULL");

        // 4. Trace correlation. Partial, because most log lines carry no trace id,
        //    and indexing the nulls would double the index for no benefit.
        builder.HasIndex(x => new { x.OrganizationId, x.TraceId })
            .HasFilter("trace_id IS NOT NULL");

        // Deliberately NOT indexed: (organization, service, environment, time).
        // That question is answered through log_patterns, which is orders of
        // magnitude smaller. Adding it later is one migration; carrying it now
        // would slow every insert for a query nothing runs.

        builder.HasOne(x => x.MonitoredService)
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.MonitoredServiceId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.Environment)
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.EnvironmentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Classification happens after insert, so these start null. SetNull on
        // delete keeps the sample row even if its pattern is pruned.
        builder.HasOne(x => x.LogPattern)
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.LogPatternId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.SetNull);

        builder.HasOne(x => x.Incident)
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.IncidentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class LogPatternMetricConfiguration : IEntityTypeConfiguration<LogPatternMetric>
{
    public void Configure(EntityTypeBuilder<LogPatternMetric> builder)
    {
        builder.ToTable("log_pattern_metrics");

        // One row per pattern per minute; the pair is the natural key, so no
        // surrogate is needed and the upsert has something to conflict on.
        builder.HasKey(x => new { x.LogPatternId, x.BucketStart });

        builder.Property(x => x.Count).HasDefaultValue(0L);

        // The window query: this pattern, these buckets.
        builder.HasIndex(x => new { x.OrganizationId, x.LogPatternId, x.BucketStart })
            .IsDescending(false, false, true);

        // The retention sweep, which drops buckets older than the longest
        // baseline any rule looks at.
        builder.HasIndex(x => x.BucketStart);

        builder.HasOne(x => x.LogPattern)
            .WithMany()
            .HasForeignKey(x => new { x.OrganizationId, x.LogPatternId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
