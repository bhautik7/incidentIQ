using IncidentIQ.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Environment = IncidentIQ.Domain.Entities.Environment;

namespace IncidentIQ.Persistence.Configurations;

public class MonitoredServiceConfiguration : IEntityTypeConfiguration<MonitoredService>
{
    public void Configure(EntityTypeBuilder<MonitoredService> builder)
    {
        builder.ToTable("monitored_services");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Key).HasMaxLength(100).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(x => x.Description).HasMaxLength(1000);
        builder.Property(x => x.OwnerTeam).HasMaxLength(100);
        builder.Property(x => x.IsActive).HasDefaultValue(true);

        // Ingestion resolves "payments-api" to an id on this index for every batch.
        builder.HasIndex(x => new { x.OrganizationId, x.Key }).IsUnique();

        builder.HasOne(x => x.Organization)
            .WithMany(x => x.MonitoredServices)
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class EnvironmentConfiguration : IEntityTypeConfiguration<Environment>
{
    public void Configure(EntityTypeBuilder<Environment> builder)
    {
        builder.ToTable("environments");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Key).HasMaxLength(50).IsRequired();
        builder.Property(x => x.DisplayName).HasMaxLength(100).IsRequired();

        builder.HasIndex(x => new { x.OrganizationId, x.Key }).IsUnique();

        builder.HasOne(x => x.Organization)
            .WithMany(x => x.Environments)
            .HasForeignKey(x => x.OrganizationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}

public class DeploymentConfiguration : IEntityTypeConfiguration<Deployment>
{
    public void Configure(EntityTypeBuilder<Deployment> builder)
    {
        builder.ToTable("deployments");

        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).ValueGeneratedNever();

        builder.Property(x => x.Version).HasMaxLength(100).IsRequired();
        builder.Property(x => x.CommitSha).HasMaxLength(40);
        builder.Property(x => x.DeployedBy).HasMaxLength(200);
        builder.Property(x => x.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(x => x.Metadata).HasColumnType("jsonb");

        // "What shipped just before this incident started?" - equality on the
        // service and environment, then a descending range scan on time. That
        // ordering of columns is what lets one index answer the whole question.
        builder.HasIndex(x => new { x.OrganizationId, x.MonitoredServiceId, x.EnvironmentId, x.DeployedAt })
            .IsDescending(false, false, false, true);

        builder.HasOne(x => x.MonitoredService)
            .WithMany(x => x.Deployments)
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
