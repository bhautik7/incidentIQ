using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using IncidentIQ.Domain.Entities;
using IncidentIQ.Domain.Enums;
using IncidentIQ.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Testcontainers.PostgreSql;
using Environment = IncidentIQ.Domain.Entities.Environment;

namespace IncidentIQ.Outbox.Tests;

/// <summary>
/// The transactional outbox against a real PostgreSQL instance.
///
/// Every claim the pattern makes is about what survives a crash at an awkward
/// moment, so the tests drive those moments deliberately: a rolled-back
/// transaction, a broker that refuses, a publisher that succeeds and then runs
/// again.
/// </summary>
public sealed class OutboxIntegrationTests : IAsyncLifetime
{
    private static readonly Guid TenantId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ServiceId = new("11111111-0000-0000-0000-0000000000a1");
    private static readonly Guid EnvironmentId = new("11111111-0000-0000-0000-0000000000b1");

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .WithDatabase("incidentiq_outbox_test")
        .Build();

    private IncidentIQDbContext _dbContext = null!;
    private ControllableProducer _producer = null!;
    private FakeTimeProvider _time = null!;
    private OutboxOptions _options = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        var options = new DbContextOptionsBuilder<IncidentIQDbContext>()
            .UseIncidentIQPostgres(_postgres.GetConnectionString())
            .Options;

        _dbContext = new IncidentIQDbContext(options, new StaticTenantContext(TenantId));
        await _dbContext.Database.MigrateAsync();

        _dbContext.Organizations.Add(new Organization
        {
            Id = TenantId, Name = "Acme Corp", Slug = "acme", Status = OrganizationStatus.Active
        });
        _dbContext.MonitoredServices.Add(new MonitoredService
        {
            Id = ServiceId, OrganizationId = TenantId, Key = "payments-api", DisplayName = "Payments API"
        });
        _dbContext.Environments.Add(new Environment
        {
            Id = EnvironmentId, OrganizationId = TenantId, Key = "production",
            DisplayName = "Production", Rank = 100, IsProduction = true
        });
        await _dbContext.SaveChangesAsync();

        _producer = new ControllableProducer();
        _time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        _options = new OutboxOptions { MaxAttempts = 3, RetryBaseDelayMs = 1000, BatchSize = 10 };
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private OutboxDrainer CreateDrainer() =>
        new(_dbContext, _producer, Options.Create(_options), _time, NullLogger<OutboxDrainer>.Instance);

    /// <summary>
    /// The operation the pattern exists to protect: one domain change plus one
    /// integration event, in a single transaction.
    /// </summary>
    private async Task<(Deployment Deployment, OutboxMessage Message)> RecordDeploymentAsync(
        string version = "2.31.0",
        bool rollback = false)
    {
        var writer = new OutboxWriter(_dbContext);

        var deployment = new Deployment
        {
            Id = Guid.CreateVersion7(),
            OrganizationId = TenantId,
            MonitoredServiceId = ServiceId,
            EnvironmentId = EnvironmentId,
            Version = version,
            DeployedAt = _time.GetUtcNow(),
            Status = DeploymentStatus.Succeeded
        };

        var envelope = EventEnvelope<DeploymentCreated>.Create(
            EventTypes.DeploymentCreated,
            TenantId,
            new DeploymentCreated
            {
                DeploymentId = deployment.Id,
                Service = "payments-api",
                Environment = "production",
                Version = version,
                DeployedAt = deployment.DeployedAt
            });

        OutboxMessage message = null!;

        if (rollback)
        {
            // Stands in for "the application crashed after staging both rows
            // but before the commit". Written out rather than using the helper,
            // because the helper deliberately has no way to not commit.
            var strategy = _dbContext.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await _dbContext.Database.BeginTransactionAsync();

                _dbContext.Deployments.Add(deployment);
                message = writer.Enqueue(BuildRequest(deployment, envelope));

                await _dbContext.SaveChangesAsync();
                await transaction.RollbackAsync();
            });
        }
        else
        {
            // The single transaction that makes the pattern work: domain change
            // and integration event, committed together or not at all.
            await _dbContext.ExecuteInTransactionAsync(() =>
            {
                _dbContext.Deployments.Add(deployment);
                message = writer.Enqueue(BuildRequest(deployment, envelope));
                return Task.CompletedTask;
            });
        }

        _dbContext.ChangeTracker.Clear();
        return (deployment, message);
    }

    private static OutboxEnqueueRequest BuildRequest(Deployment deployment, EventEnvelope<DeploymentCreated> envelope)
    {
        return new OutboxEnqueueRequest
        {
            OrganizationId = TenantId,
            AggregateType = nameof(Deployment),
            AggregateId = deployment.Id,
            EventType = EventTypes.DeploymentCreated,
            Topic = Topics.DeploymentsCreated,
            PartitionKey = PartitionKeys.ForService(TenantId, "payments-api"),
            SerialisedEnvelope = EventJson.Serialize(envelope),
            EventId = envelope.EventId,
            CorrelationId = envelope.CorrelationId,
            OccurredAt = envelope.OccurredAt
        };
    }

    // ---------------------------------------------------------------
    // 1. The database transaction is consistent
    // ---------------------------------------------------------------

    [Fact]
    public async Task Domain_row_and_outbox_row_commit_together()
    {
        var (deployment, _) = await RecordDeploymentAsync();

        Assert.True(await _dbContext.Deployments.AnyAsync(d => d.Id == deployment.Id));

        var message = await _dbContext.OutboxMessages.SingleAsync();
        Assert.Equal(deployment.Id, message.AggregateId);
        Assert.Equal(Topics.DeploymentsCreated, message.Topic);
        Assert.Null(message.PublishedAt);
    }

    [Fact]
    public async Task A_rolled_back_transaction_leaves_neither_row()
    {
        // The opposite failure: an event announcing something that never
        // happened. Because the event lives in the same transaction, the
        // rollback takes it too.
        await RecordDeploymentAsync(rollback: true);

        Assert.Equal(0, await _dbContext.Deployments.CountAsync());
        Assert.Equal(0, await _dbContext.OutboxMessages.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Nothing_is_published_before_the_transaction_commits()
    {
        await RecordDeploymentAsync(rollback: true);
        await CreateDrainer().DrainOnceAsync(CancellationToken.None);

        Assert.Empty(_producer.Published);
    }

    [Fact]
    public async Task A_committed_event_is_published_with_its_stored_key_and_bytes()
    {
        var (deployment, message) = await RecordDeploymentAsync();

        var result = await CreateDrainer().DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.Published);

        var published = Assert.Single(_producer.Published);
        Assert.Equal(Topics.DeploymentsCreated, published.Topic);
        Assert.Equal($"{TenantId:D}:payments-api", published.Key);

        // Byte-identical to what was committed - no reconstruction step that
        // could drift from the stored payload.
        Assert.Equal(message.Payload, published.Payload);

        var envelope = EventJson.Deserialize<DeploymentCreated>(published.Payload);
        Assert.Equal(message.EventId, envelope.EventId);
        Assert.Equal(deployment.Id, envelope.Payload.DeploymentId);
    }

    [Fact]
    public async Task Headers_carry_the_routing_fields()
    {
        var (_, message) = await RecordDeploymentAsync();
        await CreateDrainer().DrainOnceAsync(CancellationToken.None);

        var headers = Assert.Single(_producer.Published).Headers!;

        Assert.Equal(message.EventId.ToString(), headers[EventHeaders.EventId]);
        Assert.Equal(EventTypes.DeploymentCreated, headers[EventHeaders.EventType]);
        Assert.Equal(TenantId.ToString(), headers[EventHeaders.TenantId]);
        Assert.Equal(message.CorrelationId.ToString(), headers[EventHeaders.CorrelationId]);
    }

    // ---------------------------------------------------------------
    // 2. Failed publishing can be retried
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_failed_publish_leaves_the_message_pending_and_schedules_a_retry()
    {
        await RecordDeploymentAsync();
        _producer.FailWith = new InvalidOperationException("broker unreachable");

        var result = await CreateDrainer().DrainOnceAsync(CancellationToken.None);

        Assert.Equal(1, result.Failed);
        Assert.Equal(0, result.Published);

        var message = await _dbContext.OutboxMessages.AsNoTracking().SingleAsync();

        // Still pending, so it will be picked up again - the whole point.
        Assert.Null(message.PublishedAt);
        Assert.Equal(1, message.AttemptCount);
        Assert.Contains("broker unreachable", message.LastError);
        Assert.NotNull(message.NextAttemptAt);
        Assert.True(message.NextAttemptAt > _time.GetUtcNow());
    }

    [Fact]
    public async Task A_message_is_not_retried_before_its_backoff_elapses()
    {
        await RecordDeploymentAsync();
        _producer.FailWith = new InvalidOperationException("broker unreachable");
        await CreateDrainer().DrainOnceAsync(CancellationToken.None);

        _producer.FailWith = null;

        // Broker is healthy again, but the backoff has not elapsed. Without
        // next_attempt_at this would be a tight retry loop against a service
        // that has just fallen over.
        var tooSoon = await CreateDrainer().DrainOnceAsync(CancellationToken.None);
        Assert.Equal(0, tooSoon.Claimed);
        Assert.Empty(_producer.Published);

        _time.Advance(TimeSpan.FromSeconds(30));

        var afterBackoff = await CreateDrainer().DrainOnceAsync(CancellationToken.None);
        Assert.Equal(1, afterBackoff.Published);
    }

    [Fact]
    public async Task A_transient_failure_eventually_succeeds_without_losing_the_event()
    {
        var (_, message) = await RecordDeploymentAsync();

        _producer.FailWith = new InvalidOperationException("broker unreachable");
        await CreateDrainer().DrainOnceAsync(CancellationToken.None);

        _producer.FailWith = null;
        _time.Advance(TimeSpan.FromSeconds(30));
        await CreateDrainer().DrainOnceAsync(CancellationToken.None);

        var published = Assert.Single(_producer.Published);
        var envelope = EventJson.Deserialize<DeploymentCreated>(published.Payload);

        // The event id is unchanged across the retry. That is what lets a
        // consumer recognise a redelivery as the same event.
        Assert.Equal(message.EventId, envelope.EventId);

        var stored = await _dbContext.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.NotNull(stored.PublishedAt);
        Assert.Null(stored.LastError);
    }

    [Fact]
    public async Task Backoff_grows_with_each_attempt()
    {
        await RecordDeploymentAsync();
        _producer.FailWith = new InvalidOperationException("broker unreachable");

        var delays = new List<TimeSpan>();

        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await CreateDrainer().DrainOnceAsync(CancellationToken.None);

            var message = await _dbContext.OutboxMessages.AsNoTracking().SingleAsync();
            delays.Add(message.NextAttemptAt!.Value - _time.GetUtcNow());

            _dbContext.ChangeTracker.Clear();
            _time.Advance(TimeSpan.FromMinutes(10));
        }

        Assert.True(delays[1] > delays[0],
            $"expected growing backoff, got {delays[0].TotalMilliseconds}ms then {delays[1].TotalMilliseconds}ms");
    }

    [Fact]
    public async Task A_message_that_never_succeeds_is_dead_lettered_rather_than_retried_forever()
    {
        await RecordDeploymentAsync();
        _producer.FailWith = new InvalidOperationException("permanently broken");

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            await CreateDrainer().DrainOnceAsync(CancellationToken.None);
            _dbContext.ChangeTracker.Clear();
            _time.Advance(TimeSpan.FromMinutes(10));
        }

        var message = await _dbContext.OutboxMessages.AsNoTracking().SingleAsync();
        Assert.NotNull(message.DeadLetteredAt);
        Assert.Equal(_options.MaxAttempts, message.AttemptCount);

        // And it stops consuming capacity on every subsequent pass.
        _producer.FailWith = null;
        _time.Advance(TimeSpan.FromHours(1));

        var afterGivingUp = await CreateDrainer().DrainOnceAsync(CancellationToken.None);
        Assert.Equal(0, afterGivingUp.Claimed);
    }

    [Fact]
    public async Task One_failure_in_a_batch_does_not_block_the_others()
    {
        await RecordDeploymentAsync("1.0.0");
        await RecordDeploymentAsync("2.0.0");
        await RecordDeploymentAsync("3.0.0");

        _producer.FailWhen = (_, body) => body.Contains("2.0.0", StringComparison.Ordinal);

        var result = await CreateDrainer().DrainOnceAsync(CancellationToken.None);

        Assert.Equal(3, result.Claimed);
        Assert.Equal(2, result.Published);
        Assert.Equal(1, result.Failed);

        var pending = await _dbContext.OutboxMessages.AsNoTracking()
            .Where(m => m.PublishedAt == null)
            .ToListAsync();

        Assert.Single(pending);
        Assert.Equal(1, pending[0].AttemptCount);
    }

    // ---------------------------------------------------------------
    // 3. Published messages are not published again
    // ---------------------------------------------------------------

    [Fact]
    public async Task A_published_message_is_never_claimed_again()
    {
        await RecordDeploymentAsync();

        var first = await CreateDrainer().DrainOnceAsync(CancellationToken.None);
        Assert.Equal(1, first.Published);

        // The failure this guards against: a publisher that keeps finding the
        // same rows and re-emitting them forever.
        for (var pass = 0; pass < 5; pass++)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            var again = await CreateDrainer().DrainOnceAsync(CancellationToken.None);

            Assert.Equal(0, again.Claimed);
            Assert.Equal(0, again.Published);
        }

        Assert.Single(_producer.Published);
    }

    [Fact]
    public async Task Draining_an_empty_outbox_is_a_no_op()
    {
        var result = await CreateDrainer().DrainOnceAsync(CancellationToken.None);

        Assert.Equal(0, result.Claimed);
        Assert.Empty(_producer.Published);
    }

    [Fact]
    public async Task Messages_are_claimed_in_creation_order()
    {
        await RecordDeploymentAsync("1.0.0");
        await RecordDeploymentAsync("2.0.0");
        await RecordDeploymentAsync("3.0.0");

        await CreateDrainer().DrainOnceAsync(CancellationToken.None);

        var versions = _producer.Published
            .Select(p => EventJson.Deserialize<DeploymentCreated>(p.Payload).Payload.Version)
            .ToList();

        Assert.Equal(["1.0.0", "2.0.0", "3.0.0"], versions);
    }

    [Fact]
    public async Task The_batch_size_bounds_one_pass()
    {
        _options.BatchSize = 2;

        for (var i = 0; i < 5; i++)
        {
            await RecordDeploymentAsync($"{i}.0.0");
        }

        var first = await CreateDrainer().DrainOnceAsync(CancellationToken.None);
        Assert.Equal(2, first.Claimed);

        var second = await CreateDrainer().DrainOnceAsync(CancellationToken.None);
        Assert.Equal(2, second.Claimed);

        var third = await CreateDrainer().DrainOnceAsync(CancellationToken.None);
        Assert.Equal(1, third.Claimed);

        Assert.Equal(5, _producer.Published.Count);
    }

    [Fact]
    public async Task Enqueueing_the_same_event_id_twice_is_rejected_by_the_database()
    {
        var (_, message) = await RecordDeploymentAsync();

        var writer = new OutboxWriter(_dbContext);
        writer.Enqueue(new OutboxEnqueueRequest
        {
            OrganizationId = TenantId,
            AggregateType = nameof(Deployment),
            AggregateId = Guid.CreateVersion7(),
            EventType = EventTypes.DeploymentCreated,
            Topic = Topics.DeploymentsCreated,
            PartitionKey = "k",
            SerialisedEnvelope = message.Payload,
            // Same event id: a retried command that recomputed the same event.
            EventId = message.EventId,
            CorrelationId = message.CorrelationId
        });

        await Assert.ThrowsAnyAsync<DbUpdateException>(() => _dbContext.SaveChangesAsync());
    }

    [Fact]
    public void Enqueueing_without_an_event_id_is_rejected_outright()
    {
        var writer = new OutboxWriter(_dbContext);

        // Without a stable id there is nothing for a consumer to deduplicate on,
        // so this is caught at the call site rather than at publish time.
        Assert.Throws<ArgumentException>(() => writer.Enqueue(new OutboxEnqueueRequest
        {
            OrganizationId = TenantId,
            AggregateType = nameof(Deployment),
            AggregateId = Guid.CreateVersion7(),
            EventType = EventTypes.DeploymentCreated,
            Topic = Topics.DeploymentsCreated,
            PartitionKey = "k",
            SerialisedEnvelope = "{}",
            EventId = Guid.Empty,
            CorrelationId = Guid.CreateVersion7()
        }));
    }
}
