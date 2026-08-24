using System.Linq.Expressions;
using IncidentIQ.Domain.Abstractions;
using IncidentIQ.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Environment = IncidentIQ.Domain.Entities.Environment;

namespace IncidentIQ.Persistence;

public class IncidentIQDbContext(DbContextOptions<IncidentIQDbContext> options, ITenantContext tenantContext)
    : DbContext(options)
{
    public const string Schema = "public";

    private readonly ITenantContext _tenantContext = tenantContext;

    public DbSet<Organization> Organizations => Set<Organization>();
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<MonitoredService> MonitoredServices => Set<MonitoredService>();
    public DbSet<Environment> Environments => Set<Environment>();
    public DbSet<Deployment> Deployments => Set<Deployment>();
    public DbSet<LogEvent> LogEvents => Set<LogEvent>();
    public DbSet<LogPattern> LogPatterns => Set<LogPattern>();
    public DbSet<Incident> Incidents => Set<Incident>();
    public DbSet<IncidentEvent> IncidentEvents => Set<IncidentEvent>();
    public DbSet<AiAnalysis> AiAnalyses => Set<AiAnalysis>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Required by AiAnalyses.embedding. Declared here so the migration
        // creates it, which is what makes a throwaway test database work.
        modelBuilder.HasPostgresExtension("vector");
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IncidentIQDbContext).Assembly);

        ApplyTenantQueryFilters(modelBuilder);

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>
    /// Adds <c>WHERE organization_id = @current</c> to every tenant-scoped entity.
    ///
    /// This is the application-side half of tenant isolation; the database-side
    /// half is the composite foreign keys in the entity configurations, which
    /// make a cross-tenant reference impossible to even write. Belt and braces
    /// is the right posture here: a query filter can be bypassed with
    /// IgnoreQueryFilters, a foreign key cannot.
    /// </summary>
    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantScoped).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var parameter = Expression.Parameter(entityType.ClrType, "e");
            var organizationId = Expression.Property(parameter, nameof(ITenantScoped.OrganizationId));

            // Compared as Guid? so that a null tenant context yields
            // "organization_id = NULL", which matches no rows. Fail closed.
            var currentOrganizationId = Expression.Property(
                Expression.Constant(this),
                nameof(CurrentOrganizationId));

            var body = Expression.Equal(
                Expression.Convert(organizationId, typeof(Guid?)),
                currentOrganizationId);

            modelBuilder.Entity(entityType.ClrType)
                .HasQueryFilter(Expression.Lambda(body, parameter));
        }
    }

    /// <summary>Read by the query filters; public so the expression tree can bind to it.</summary>
    public Guid? CurrentOrganizationId => _tenantContext.OrganizationId;

    public override int SaveChanges()
    {
        StampTimestamps();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTimestamps();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>
    /// Maintains CreatedAt/UpdatedAt centrally. Callers cannot forget them, and
    /// cannot set them to a client clock - every timestamp in the database comes
    /// from the same source and is stored as UTC.
    /// </summary>
    private void StampTimestamps()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var entry in ChangeTracker.Entries())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    if (entry.Entity is ICreatedAt created && created.CreatedAt == default)
                    {
                        created.CreatedAt = now;
                    }

                    if (entry.Entity is IAuditable addedAuditable)
                    {
                        if (addedAuditable.CreatedAt == default)
                        {
                            addedAuditable.CreatedAt = now;
                        }

                        addedAuditable.UpdatedAt = addedAuditable.CreatedAt;
                    }

                    break;

                case EntityState.Modified:
                    if (entry.Entity is IAuditable modifiedAuditable)
                    {
                        modifiedAuditable.UpdatedAt = now;
                    }

                    break;
            }
        }
    }
}
