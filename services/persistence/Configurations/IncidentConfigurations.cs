using IncidentIQ.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Environment = IncidentIQ.Domain.Entities.Environment;

namespace IncidentIQ.Persistence.Configurations;

public class IncidentConfiguration : IEntityTypeConfiguration<Incident>
{
    public void Configure(EntityTypeBuilder<Incident> builder)
    {
        builder.ToTable("incidents");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Title).HasMaxLength(500).IsRequired();
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.DetectionRule).HasConversion<string>().HasMaxLength(40).IsRequired();
        builder.Property(x => x.DedupeKey).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Severity).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.OccurrenceCount).HasDefaultValue(0L);
        builder.Property(x => x.ResolutionNotes).HasColumnType("text");

        // The central correctness constraint: at most one ACTIVE incident per
        // dedupe key. Two detector replicas processing the same burst cannot
        // both open one - the second insert loses the race and is turned into
        // an update of the first.
        //
        // Keyed on dedupe_key rather than log_pattern_id so that rules which
        // are not pattern-scoped, such as the server-error spike, get the same
        // guarantee. Resolved and ignored incidents are excluded, so the same
        // problem can recur next month as a new incident.
        builder.HasIndex(x => new { x.OrganizationId, x.DedupeKey })
            .IsUnique()
            .HasFilter("status IN ('Detected', 'Investigating')")
            .HasDatabaseName("ux_incidents_active_dedupe_key");

        // Reopen-within-cooldown lookup: the most recent incident for a key,
        // whatever its status.
        builder.HasIndex(x => new { x.OrganizationId, x.DedupeKey, x.LastSeenAt })
            .IsDescending(false, false, true);

        // The dashboard's main query: open incidents, most recent first.
        builder.HasIndex(x => new { x.OrganizationId, x.Status, x.LastSeenAt })
            .IsDescending(false, false, true);


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
            .WithMany(x => x.Incidents)
            .HasForeignKey(x => new { x.OrganizationId, x.LogPatternId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<User>()
            .WithMany()
            .HasForeignKey(x => x.InvestigatingUserId)
            .OnDelete(DeleteBehavior.SetNull);

        // Restrict, not Cascade: deleting deployment history must never silently
        // delete the incidents that history explains.
        builder.HasOne(x => x.SuspectedDeployment)
            .WithMany()
            .HasForeignKey(x => x.SuspectedDeploymentId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}

public class IncidentEventConfiguration : IEntityTypeConfiguration<IncidentEvent>
{
    public void Configure(EntityTypeBuilder<IncidentEvent> builder)
    {
        builder.ToTable("incident_events");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).UseIdentityAlwaysColumn();

        builder.Property(x => x.Type).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(x => x.ActorType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Message).HasColumnType("text");
        builder.Property(x => x.Data).HasColumnType("jsonb");

        // The timeline: ascending, because a timeline is read oldest-first.
        builder.HasIndex(x => new { x.OrganizationId, x.IncidentId, x.OccurredAt });

        builder.HasOne(x => x.Incident)
            .WithMany(x => x.Events)
            .HasForeignKey(x => new { x.OrganizationId, x.IncidentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(x => x.ActorUser)
            .WithMany()
            .HasForeignKey(x => x.ActorUserId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}

public class AiAnalysisConfiguration : IEntityTypeConfiguration<AiAnalysis>
{
    /// <summary>
    /// Dimension of the embedding column. Fixed at the schema level because
    /// pgvector requires it; changing model families means a new column and a
    /// backfill, which is exactly what AnalysisVersion is for.
    /// </summary>
    public const int EmbeddingDimensions = 1536;

    public void Configure(EntityTypeBuilder<AiAnalysis> builder)
    {
        builder.ToTable("ai_analyses");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.AnalysisVersion).HasDefaultValue(1);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();

        builder.Property(x => x.Embedding).HasColumnType($"vector({EmbeddingDimensions})");

        builder.Property(x => x.EmbeddingModel).HasMaxLength(100);
        builder.Property(x => x.ModelProvider).HasMaxLength(50);
        builder.Property(x => x.ModelName).HasMaxLength(100);
        builder.Property(x => x.Summary).HasColumnType("text");
        builder.Property(x => x.ProbableCause).HasColumnType("text");
        builder.Property(x => x.SuggestedActions).HasColumnType("jsonb");
        builder.Property(x => x.SimilarIncidents).HasColumnType("jsonb");
        builder.Property(x => x.Confidence).HasPrecision(4, 3);
        builder.Property(x => x.Error).HasColumnType("text");

        // Idempotency for the AI worker: re-running version 1 of an analysis
        // writes nothing rather than producing a second row.
        builder.HasIndex(x => new { x.IncidentId, x.AnalysisVersion }).IsUnique();

        // The similarity search that justifies running pgvector at all:
        //   ORDER BY embedding <=> $1 LIMIT 5
        // HNSW rather than IVFFlat because it needs no training pass and stays
        // accurate as rows trickle in one incident at a time. Cosine distance,
        // matching how the embeddings are normalised.
        builder.HasIndex(x => x.Embedding)
            .HasMethod("hnsw")
            .HasOperators("vector_cosine_ops")
            .HasDatabaseName("ix_ai_analyses_embedding_hnsw");

        builder.HasOne(x => x.Incident)
            .WithMany(x => x.Analyses)
            .HasForeignKey(x => new { x.OrganizationId, x.IncidentId })
            .HasPrincipalKey(x => new { x.OrganizationId, x.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
