using IncidentIQ.Domain.Entities;
using IncidentIQ.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Environment = IncidentIQ.Domain.Entities.Environment;

namespace IncidentIQ.Persistence;

/// <summary>
/// Seeds reference data and, optionally, a small development dataset.
///
/// Both passes are idempotent: they check for existing rows and do nothing if
/// present, so running them on every startup is safe.
///
/// Writes bypass the tenant query filters by construction - inserts are not
/// filtered - but every row is given an explicit OrganizationId, which is what
/// the composite foreign keys then enforce.
/// </summary>
public class DatabaseSeeder(IncidentIQDbContext dbContext, ILogger<DatabaseSeeder> logger)
{
    /// <summary>Platform roles. Safe and expected in every environment.</summary>
    public async Task SeedReferenceDataAsync(CancellationToken cancellationToken = default)
    {
        var existing = await dbContext.Roles.Select(r => r.Id).ToListAsync(cancellationToken);
        var missing = SeedData.SystemRoles().Where(r => !existing.Contains(r.Id)).ToList();

        if (missing.Count == 0)
        {
            logger.LogInformation("Reference data already present; nothing to seed.");
            return;
        }

        dbContext.Roles.AddRange(missing);
        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Seeded {Count} system role(s).", missing.Count);
    }

    /// <summary>
    /// Two organizations with real-looking data, so tenant isolation is
    /// visible rather than theoretical. Development only.
    /// </summary>
    public async Task SeedDevelopmentDataAsync(CancellationToken cancellationToken = default)
    {
        if (await dbContext.Organizations.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            logger.LogInformation("Development data already present; nothing to seed.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var deployedAt = now.AddHours(-3);
        var incidentStart = deployedAt.AddMinutes(4);

        // ---------------- Organization 1: Acme ----------------

        dbContext.Organizations.Add(new Organization
        {
            Id = SeedIds.Acme.OrganizationId,
            Name = "Acme Corp",
            Slug = "acme",
            Status = OrganizationStatus.Active
        });

        dbContext.Users.AddRange(
            new User
            {
                Id = SeedIds.Acme.OwnerUserId,
                OrganizationId = SeedIds.Acme.OrganizationId,
                Email = "owner@acme.test",
                DisplayName = "Ada Owner",
                Status = UserStatus.Active
            },
            new User
            {
                Id = SeedIds.Acme.ResponderUserId,
                OrganizationId = SeedIds.Acme.OrganizationId,
                Email = "responder@acme.test",
                DisplayName = "Ravi Responder",
                Status = UserStatus.Active
            });

        dbContext.UserRoles.AddRange(
            new UserRole { OrganizationId = SeedIds.Acme.OrganizationId, UserId = SeedIds.Acme.OwnerUserId, RoleId = SeedIds.Roles.Owner, AssignedAt = now },
            new UserRole { OrganizationId = SeedIds.Acme.OrganizationId, UserId = SeedIds.Acme.ResponderUserId, RoleId = SeedIds.Roles.Responder, AssignedAt = now });

        dbContext.Environments.AddRange(
            new Environment { Id = SeedIds.Acme.ProductionId, OrganizationId = SeedIds.Acme.OrganizationId, Key = "production", DisplayName = "Production", Rank = 100, IsProduction = true },
            new Environment { Id = SeedIds.Acme.StagingId, OrganizationId = SeedIds.Acme.OrganizationId, Key = "staging", DisplayName = "Staging", Rank = 50, IsProduction = false });

        dbContext.MonitoredServices.AddRange(
            new MonitoredService { Id = SeedIds.Acme.PaymentsApiId, OrganizationId = SeedIds.Acme.OrganizationId, Key = "payments-api", DisplayName = "Payments API", OwnerTeam = "Payments" },
            new MonitoredService { Id = SeedIds.Acme.OrdersApiId, OrganizationId = SeedIds.Acme.OrganizationId, Key = "orders-api", DisplayName = "Orders API", OwnerTeam = "Fulfilment" });

        dbContext.Deployments.Add(new Deployment
        {
            Id = SeedIds.Acme.DeploymentId,
            OrganizationId = SeedIds.Acme.OrganizationId,
            MonitoredServiceId = SeedIds.Acme.PaymentsApiId,
            EnvironmentId = SeedIds.Acme.ProductionId,
            Version = "2.31.0",
            CommitSha = "9f4c2ab7d31e05b6c8a1f2e3d4b5a6c7d8e9f001",
            DeployedBy = "ci-pipeline",
            DeployedAt = deployedAt,
            Status = DeploymentStatus.Succeeded,
            Metadata = """{"pipeline":"github-actions","runId":"1842","pullRequest":417}"""
        });

        // The worked example from the architecture: a connection-pool exhaustion
        // that starts four minutes after a deployment.
        dbContext.LogPatterns.AddRange(
            new LogPattern
            {
                Id = SeedIds.Acme.PoolPatternId,
                OrganizationId = SeedIds.Acme.OrganizationId,
                MonitoredServiceId = SeedIds.Acme.PaymentsApiId,
                EnvironmentId = SeedIds.Acme.ProductionId,
                Fingerprint = "a1b2c3d4e5f60718293a4b5c6d7e8f901a2b3c4d5e6f708192a3b4c5d6e7f801",
                Level = LogEventLevel.Error,
                ExceptionType = "Npgsql.NpgsqlException",
                MessageTemplate = "The connection pool has been exhausted, either raise MaxPoolSize (currently <NUM>) or Timeout (currently <NUM> seconds)",
                SampleMessage = "The connection pool has been exhausted, either raise MaxPoolSize (currently 100) or Timeout (currently 15 seconds)",
                TopStackFrames = "at Npgsql.PoolingDataSource.Get(...)\nat Npgsql.NpgsqlConnection.Open(...)",
                OccurrenceCount = 4200,
                FirstSeenAt = incidentStart,
                LastSeenAt = incidentStart.AddMinutes(3)
            },
            new LogPattern
            {
                Id = SeedIds.Acme.TimeoutPatternId,
                OrganizationId = SeedIds.Acme.OrganizationId,
                MonitoredServiceId = SeedIds.Acme.OrdersApiId,
                EnvironmentId = SeedIds.Acme.ProductionId,
                Fingerprint = "b2c3d4e5f60718293a4b5c6d7e8f901a2b3c4d5e6f708192a3b4c5d6e7f80102",
                Level = LogEventLevel.Error,
                ExceptionType = "System.Net.Http.HttpRequestException",
                MessageTemplate = "Response status code does not indicate success: <NUM> (Bad Gateway) from payments-api",
                SampleMessage = "Response status code does not indicate success: 502 (Bad Gateway) from payments-api",
                OccurrenceCount = 318,
                FirstSeenAt = incidentStart.AddSeconds(40),
                LastSeenAt = incidentStart.AddMinutes(3)
            });

        dbContext.Incidents.Add(new Incident
        {
            Id = SeedIds.Acme.IncidentId,
            OrganizationId = SeedIds.Acme.OrganizationId,
            MonitoredServiceId = SeedIds.Acme.PaymentsApiId,
            EnvironmentId = SeedIds.Acme.ProductionId,
            LogPatternId = SeedIds.Acme.PoolPatternId,
            Title = "payments-api: connection pool exhausted",
            Status = IncidentStatus.Open,
            Severity = IncidentSeverity.Critical,
            OccurrenceCount = 4200,
            FirstSeenAt = incidentStart,
            LastSeenAt = incidentStart.AddMinutes(3),
            SuspectedDeploymentId = SeedIds.Acme.DeploymentId
        });

        dbContext.IncidentEvents.AddRange(
            new IncidentEvent
            {
                OrganizationId = SeedIds.Acme.OrganizationId,
                IncidentId = SeedIds.Acme.IncidentId,
                Type = IncidentEventType.Created,
                OccurredAt = incidentStart,
                ActorType = ActorType.System,
                Message = "Incident opened from pattern a1b2c3d4."
            },
            new IncidentEvent
            {
                OrganizationId = SeedIds.Acme.OrganizationId,
                IncidentId = SeedIds.Acme.IncidentId,
                Type = IncidentEventType.Escalated,
                OccurredAt = incidentStart.AddMinutes(1),
                ActorType = ActorType.System,
                Message = "Occurrence count passed 1000; severity raised to Critical.",
                Data = """{"from":"High","to":"Critical","occurrenceCount":1000}"""
            });

        dbContext.AiAnalyses.Add(new AiAnalysis
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = SeedIds.Acme.OrganizationId,
            IncidentId = SeedIds.Acme.IncidentId,
            AnalysisVersion = 1,
            Status = AiAnalysisStatus.Completed,
            ModelProvider = "anthropic",
            ModelName = "claude-sonnet-5",
            EmbeddingModel = "text-embedding-3-small",
            Summary = "payments-api exhausted its PostgreSQL connection pool four minutes after release 2.31.0.",
            ProbableCause = "The DbContext service lifetime changed in 2.31.0, so connections are no longer returned to the pool.",
            SuggestedActions = """["Check the DI registration for DbContext in 2.31.0","Compare MaxPoolSize against the new concurrency","Consider rolling back 2.31.0"]""",
            SimilarIncidents = """[]""",
            Confidence = 0.870m,
            PromptTokens = 1840,
            CompletionTokens = 260,
            LatencyMs = 4120,
            CompletedAt = incidentStart.AddMinutes(1)
        });

        // A handful of sampled log events, which is exactly the point: 4,200
        // occurrences are represented by a few rows plus a counter.
        var samples = new[] { 0, 1, 2 }.Select(i => new LogEvent
        {
            OrganizationId = SeedIds.Acme.OrganizationId,
            EventId = Guid.CreateVersion7(),
            MonitoredServiceId = SeedIds.Acme.PaymentsApiId,
            EnvironmentId = SeedIds.Acme.ProductionId,
            LogPatternId = SeedIds.Acme.PoolPatternId,
            IncidentId = SeedIds.Acme.IncidentId,
            OccurredAt = incidentStart.AddSeconds(i * 30),
            ReceivedAt = incidentStart.AddSeconds(i * 30 + 1),
            Level = LogEventLevel.Error,
            Message = "The connection pool has been exhausted, either raise MaxPoolSize (currently 100) or Timeout (currently 15 seconds)",
            ExceptionType = "Npgsql.NpgsqlException",
            StackTrace = "at Npgsql.PoolingDataSource.Get(...)",
            TraceId = $"trace-{i:D4}",
            Host = $"payments-api-7d9f-x4k{i}",
            Properties = """{"deploymentVersion":"2.31.0","pod":"payments-api-7d9f"}"""
        });
        dbContext.LogEvents.AddRange(samples);

        dbContext.AuditLogs.Add(new AuditLog
        {
            OrganizationId = SeedIds.Acme.OrganizationId,
            ActorType = ActorType.User,
            ActorUserId = SeedIds.Acme.OwnerUserId,
            Action = "monitored_service.created",
            EntityType = "MonitoredService",
            EntityId = SeedIds.Acme.PaymentsApiId.ToString(),
            Changes = """{"key":{"from":null,"to":"payments-api"}}""",
            IpAddress = "203.0.113.24",
            OccurredAt = now.AddDays(-30)
        });

        // ---------------- Organization 2: Globex ----------------
        // Exists so that "Acme cannot see this" is testable.

        dbContext.Organizations.Add(new Organization
        {
            Id = SeedIds.Globex.OrganizationId,
            Name = "Globex Industries",
            Slug = "globex",
            Status = OrganizationStatus.Active
        });

        dbContext.Users.Add(new User
        {
            Id = SeedIds.Globex.OwnerUserId,
            OrganizationId = SeedIds.Globex.OrganizationId,
            // Same local part as Acme's owner: proves email uniqueness is per organization.
            Email = "owner@globex.test",
            DisplayName = "Gina Owner",
            Status = UserStatus.Active
        });

        dbContext.UserRoles.Add(new UserRole
        {
            OrganizationId = SeedIds.Globex.OrganizationId,
            UserId = SeedIds.Globex.OwnerUserId,
            RoleId = SeedIds.Roles.Owner,
            AssignedAt = now
        });

        dbContext.Environments.Add(new Environment
        {
            Id = SeedIds.Globex.ProductionId,
            OrganizationId = SeedIds.Globex.OrganizationId,
            Key = "production",
            DisplayName = "Production",
            Rank = 100,
            IsProduction = true
        });

        dbContext.MonitoredServices.Add(new MonitoredService
        {
            Id = SeedIds.Globex.ShippingApiId,
            OrganizationId = SeedIds.Globex.OrganizationId,
            Key = "shipping-api",
            DisplayName = "Shipping API",
            OwnerTeam = "Logistics"
        });

        dbContext.LogPatterns.Add(new LogPattern
        {
            Id = SeedIds.Globex.PatternId,
            OrganizationId = SeedIds.Globex.OrganizationId,
            MonitoredServiceId = SeedIds.Globex.ShippingApiId,
            EnvironmentId = SeedIds.Globex.ProductionId,
            Fingerprint = "c3d4e5f60718293a4b5c6d7e8f901a2b3c4d5e6f708192a3b4c5d6e7f8010203",
            Level = LogEventLevel.Error,
            ExceptionType = "System.TimeoutException",
            MessageTemplate = "Carrier rate lookup timed out after <NUM>ms",
            SampleMessage = "Carrier rate lookup timed out after 3000ms",
            OccurrenceCount = 57,
            FirstSeenAt = now.AddHours(-8),
            LastSeenAt = now.AddHours(-1)
        });

        dbContext.Incidents.Add(new Incident
        {
            Id = SeedIds.Globex.IncidentId,
            OrganizationId = SeedIds.Globex.OrganizationId,
            MonitoredServiceId = SeedIds.Globex.ShippingApiId,
            EnvironmentId = SeedIds.Globex.ProductionId,
            LogPatternId = SeedIds.Globex.PatternId,
            Title = "shipping-api: carrier rate lookup timeouts",
            Status = IncidentStatus.Open,
            Severity = IncidentSeverity.Medium,
            OccurrenceCount = 57,
            FirstSeenAt = now.AddHours(-8),
            LastSeenAt = now.AddHours(-1)
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Seeded development data for 2 organizations.");
    }
}
