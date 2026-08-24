using IncidentIQ.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IncidentIQ.Persistence.Configurations;

public class OutboxMessageConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        builder.ToTable("outbox_messages");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityAlwaysColumn();

        builder.Property(x => x.AggregateType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.EventType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.Headers).HasColumnType("jsonb");
        builder.Property(x => x.AttemptCount).HasDefaultValue(0);
        builder.Property(x => x.LastError).HasColumnType("text");

        // The publisher's only query: oldest unpublished first, with
        // FOR UPDATE SKIP LOCKED so several replicas can drain it concurrently.
        // Partial, so the index contains only the backlog - typically a handful
        // of rows - no matter how many millions have already been published.
        builder.HasIndex(x => x.CreatedAt)
            .HasFilter("published_at IS NULL")
            .HasDatabaseName("ix_outbox_messages_pending");

        // No foreign key to organizations. The outbox must remain writable and
        // drainable even while the rows it references are being reorganised,
        // and its retention is driven by publication, not by the aggregate's life.
    }
}

public class ProcessedEventConfiguration : IEntityTypeConfiguration<ProcessedEvent>
{
    public void Configure(EntityTypeBuilder<ProcessedEvent> builder)
    {
        builder.ToTable("processed_events");

        // The same message is legitimately processed once per consumer group;
        // only a repeat within one group is a duplicate.
        builder.HasKey(x => new { x.ConsumerGroup, x.EventId });

        builder.Property(x => x.ConsumerGroup).HasMaxLength(100).IsRequired();

        // Retention sweep: delete rows past the Kafka retention window, after
        // which redelivery is impossible and the record is dead weight.
        builder.HasIndex(x => x.ExpiresAt);
    }
}

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityAlwaysColumn();

        builder.Property(x => x.ActorType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Action).HasMaxLength(100).IsRequired();
        builder.Property(x => x.EntityType).HasMaxLength(100).IsRequired();
        builder.Property(x => x.EntityId).HasMaxLength(100).IsRequired();
        builder.Property(x => x.Changes).HasColumnType("jsonb");
        builder.Property(x => x.IpAddress).HasMaxLength(45);
        builder.Property(x => x.UserAgent).HasMaxLength(500);

        // "What happened in this organization recently?"
        builder.HasIndex(x => new { x.OrganizationId, x.OccurredAt }).IsDescending(false, true);

        // "Who touched this specific record?"
        builder.HasIndex(x => new { x.OrganizationId, x.EntityType, x.EntityId });

        // Restrict: deleting a user must not erase the audit trail of what they did.
        builder.HasOne(x => x.ActorUser)
            .WithMany()
            .HasForeignKey(x => x.ActorUserId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
