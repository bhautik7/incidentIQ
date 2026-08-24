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
