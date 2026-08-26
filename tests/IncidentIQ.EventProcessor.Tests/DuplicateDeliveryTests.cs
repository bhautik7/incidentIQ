using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using IncidentIQ.Domain.Entities;
using IncidentIQ.Domain.Enums;
using IncidentIQ.EventProcessor.Processing;
using IncidentIQ.Messaging;
using IncidentIQ.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Environment = IncidentIQ.Domain.Entities.Environment;

namespace IncidentIQ.EventProcessor.Tests;

/// <summary>
/// Proves the idempotency claim against a real PostgreSQL instance.
///
/// Kafka guarantees at-least-once delivery, which means duplicates are not an
/// edge case - they are the normal consequence of a rebalance, a crash between
/// the database commit and the offset commit, or a dead-letter replay. The
/// question is never "will this batch arrive twice" but "what happens when it
/// does".
/// </summary>
public sealed class DuplicateDeliveryTests : IAsyncLifetime
{
    private static readonly Guid TenantId = new("11111111-1111-1111-1111-111111111111");

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16")
        .WithDatabase("incidentiq_test")
        .Build();

    private IncidentIQDbContext _dbContext = null!;
    private LogReceivedBatchHandler _handler = null!;
    private RecordingProducer _producer = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();

        TopologyResolver.ClearCache();

        var options = new DbContextOptionsBuilder<IncidentIQDbContext>()
            .UseIncidentIQPostgres(_postgres.GetConnectionString())
            .Options;

        _dbContext = new IncidentIQDbContext(options, new StaticTenantContext(TenantId));
        await _dbContext.Database.MigrateAsync();

        _dbContext.Organizations.Add(new Organization
        {
            Id = TenantId,
            Name = "Acme Corp",
            Slug = "acme",
            Status = OrganizationStatus.Active
        });
        await _dbContext.SaveChangesAsync();

        _producer = new RecordingProducer();

        _handler = new LogReceivedBatchHandler(
            new TopologyResolver(_dbContext, NullLogger<TopologyResolver>.Instance),
            new LogBatchWriter(_dbContext, NullLogger<LogBatchWriter>.Instance),
            _producer,
            Options.Create(new ProcessingOptions()),
            NullLogger<LogReceivedBatchHandler>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _dbContext.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    private static EventBatchItem<LogReceived> Event(
        Guid logEventId,
        string message = "Connection timeout for user 18273",
        string service = "payments-api",
        long offset = 0)
    {
        var envelope = EventEnvelope<LogReceived>.Create(
            EventTypes.LogReceived,
            TenantId,
            new LogReceived
            {
                LogEventId = logEventId,
                Service = service,
                Environment = "production",
                Level = LogSeverity.Error,
                Message = message,
                Timestamp = DateTimeOffset.UtcNow,
                ExceptionType = "System.TimeoutException",
                StackTrace = "at Payments.Charge(Order o) in /src/Payments.cs:line 42"
            });

        return new EventBatchItem<LogReceived>(
            envelope,
            new EventContext(Topics.LogsRaw, 0, offset, $"{TenantId:D}:{service}", DateTimeOffset.UtcNow, 1));
    }

    private async Task<(long Count, int Samples)> ReadPatternAsync(string message = "Connection timeout for user 18273")
    {
        var pattern = await _dbContext.LogPatterns.AsNoTracking().SingleAsync();
        var samples = await _dbContext.LogEvents.AsNoTracking().CountAsync();
        return (pattern.OccurrenceCount, samples);
    }

    [Fact]
    public async Task Redelivering_the_same_batch_inserts_nothing_twice()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.CreateVersion7()).ToArray();
        var batch = ids.Select((id, i) => Event(id, offset: i)).ToList();

        await _handler.HandleBatchAsync(batch, CancellationToken.None);

        var afterFirst = await ReadPatternAsync();
        Assert.Equal(5, afterFirst.Count);
        Assert.Equal(5, afterFirst.Samples);

        // Exactly what Kafka does after a rebalance: the same messages again.
        await _handler.HandleBatchAsync(batch, CancellationToken.None);

        var afterSecond = await ReadPatternAsync();

        // The counter is the sharp end of this. An incident claiming 10
        // occurrences when there were 5 destroys confidence in the whole page.
        Assert.Equal(5, afterSecond.Count);
        Assert.Equal(5, afterSecond.Samples);
        Assert.Equal(1, await _dbContext.LogPatterns.CountAsync());
    }

    [Fact]
    public async Task Redelivery_is_recorded_as_such_rather_than_silently_ignored()
    {
        var batch = new[] { Event(Guid.CreateVersion7()) };

        await _handler.HandleBatchAsync(batch, CancellationToken.None);
        await _handler.HandleBatchAsync(batch, CancellationToken.None);

        var processed = await _dbContext.ProcessedEvents.AsNoTracking().ToListAsync();

        // One row per (consumer group, logical event) - the record that makes
        // the second delivery a no-op.
        Assert.Single(processed);
        Assert.Equal(ConsumerGroups.IncidentProcessor, processed[0].ConsumerGroup);
    }

    [Fact]
    public async Task A_duplicate_inside_a_single_batch_is_counted_once()
    {
        // A client that retried into the same poll window puts the same logical
        // event in one batch twice. Deduplicating only across batches would
        // miss it.
        var id = Guid.CreateVersion7();
        var batch = new[] { Event(id, offset: 0), Event(id, offset: 1) };

        await _handler.HandleBatchAsync(batch, CancellationToken.None);

        var result = await ReadPatternAsync();
        Assert.Equal(1, result.Count);
        Assert.Equal(1, result.Samples);
    }

    [Fact]
    public async Task A_partially_overlapping_redelivery_counts_only_the_new_events()
    {
        var shared = Enumerable.Range(0, 3).Select(_ => Guid.CreateVersion7()).ToArray();
        var extra = Enumerable.Range(0, 2).Select(_ => Guid.CreateVersion7()).ToArray();

        await _handler.HandleBatchAsync(shared.Select((id, i) => Event(id, offset: i)).ToList(), CancellationToken.None);
        Assert.Equal(3, (await ReadPatternAsync()).Count);

        // Overlapping redelivery: three already seen, two genuinely new.
        var overlapping = shared.Concat(extra).Select((id, i) => Event(id, offset: i)).ToList();
        await _handler.HandleBatchAsync(overlapping, CancellationToken.None);

        Assert.Equal(5, (await ReadPatternAsync()).Count);
    }

    [Fact]
    public async Task Distinct_events_sharing_a_fingerprint_all_count()
    {
        // The other half of the guarantee: deduplication must not swallow
        // genuinely distinct occurrences of the same error, or the count that
        // makes an incident urgent would always read 1.
        var batch = Enumerable.Range(0, 20)
            .Select(i => Event(Guid.CreateVersion7(), message: $"Connection timeout for user {1000 + i}", offset: i))
            .ToList();

        await _handler.HandleBatchAsync(batch, CancellationToken.None);

        var pattern = await _dbContext.LogPatterns.AsNoTracking().SingleAsync();
        Assert.Equal(20, pattern.OccurrenceCount);
        Assert.Equal("Connection timeout for user {NUM}", pattern.MessageTemplate);
    }

    [Fact]
    public async Task The_unique_index_still_holds_when_processed_events_are_pruned()
    {
        // processed_events expires after the Kafka retention window. The unique
        // index on (organization_id, event_id) is the durable backstop that
        // survives that pruning.
        var id = Guid.CreateVersion7();
        await _handler.HandleBatchAsync([Event(id)], CancellationToken.None);

        await _dbContext.Database.ExecuteSqlRawAsync("DELETE FROM processed_events;");

        await _handler.HandleBatchAsync([Event(id)], CancellationToken.None);

        // The sample row was not duplicated, because the database refused it.
        Assert.Equal(1, await _dbContext.LogEvents.CountAsync());
    }

    [Fact]
    public async Task Samples_are_capped_while_the_counter_keeps_climbing()
    {
        // log_events is a sample, not an archive; log_patterns.occurrence_count
        // is the authoritative figure.
        var batch = Enumerable.Range(0, LogBatchWriter.SamplesPerPattern + 15)
            .Select(i => Event(Guid.CreateVersion7(), message: $"Connection timeout for user {i}", offset: i))
            .ToList();

        await _handler.HandleBatchAsync(batch, CancellationToken.None);

        var pattern = await _dbContext.LogPatterns.AsNoTracking().SingleAsync();

        Assert.Equal(LogBatchWriter.SamplesPerPattern + 15, pattern.OccurrenceCount);
        Assert.Equal(LogBatchWriter.SamplesPerPattern, await _dbContext.LogEvents.CountAsync());
    }

    [Fact]
    public async Task Services_and_environments_are_created_on_first_sight()
    {
        await _handler.HandleBatchAsync([Event(Guid.CreateVersion7(), service: "brand-new-api")], CancellationToken.None);

        Assert.True(await _dbContext.MonitoredServices.AnyAsync(s => s.Key == "brand-new-api"));
        Assert.True(await _dbContext.Environments.AnyAsync(e => e.Key == "production"));
    }

    [Fact]
    public async Task Every_valid_event_is_announced_on_logs_normalized()
    {
        var batch = new[] { Event(Guid.CreateVersion7()), Event(Guid.CreateVersion7(), offset: 1) };

        await _handler.HandleBatchAsync(batch, CancellationToken.None);

        Assert.Equal(2, _producer.Published.Count);
        Assert.All(_producer.Published, p => Assert.Equal(Topics.LogsNormalized, p.Topic));

        var envelope = Assert.IsType<EventEnvelope<LogNormalized>>(_producer.Published[0].Envelope);
        Assert.Equal(EventTypes.LogNormalized, envelope.EventType);
        Assert.Equal("Connection timeout for user {NUM}", envelope.Payload.MessageTemplate);
        Assert.Equal(64, envelope.Payload.Fingerprint.Length);
    }

    [Fact]
    public async Task An_unknown_organization_is_permanent_not_retryable()
    {
        var envelope = EventEnvelope<LogReceived>.Create(
            EventTypes.LogReceived,
            Guid.CreateVersion7(),
            new LogReceived
            {
                LogEventId = Guid.CreateVersion7(),
                Service = "payments-api",
                Environment = "production",
                Level = LogSeverity.Error,
                Message = "orphan",
                Timestamp = DateTimeOffset.UtcNow
            });

        var item = new EventBatchItem<LogReceived>(
            envelope, new EventContext(Topics.LogsRaw, 0, 0, "k", DateTimeOffset.UtcNow, 1));

        // Retrying will never make the organization exist, so the consumer must
        // dead-letter rather than block the partition.
        await Assert.ThrowsAsync<PermanentEventException>(
            () => _handler.HandleBatchAsync([item], CancellationToken.None));
    }

    [Fact]
    public async Task An_unsupported_event_version_is_permanent()
    {
        var envelope = EventEnvelope<LogReceived>.Create(
            EventTypes.LogReceived, TenantId,
            new LogReceived
            {
                LogEventId = Guid.CreateVersion7(),
                Service = "payments-api",
                Environment = "production",
                Level = LogSeverity.Error,
                Message = "from the future",
                Timestamp = DateTimeOffset.UtcNow
            },
            eventVersion: 99);

        var item = new EventBatchItem<LogReceived>(
            envelope, new EventContext(Topics.LogsRaw, 0, 0, "k", DateTimeOffset.UtcNow, 1));

        await Assert.ThrowsAsync<PermanentEventException>(
            () => _handler.HandleBatchAsync([item], CancellationToken.None));
    }
}

/// <summary>Captures published events so the announce step can be asserted without a broker.</summary>
internal sealed class RecordingProducer : IEventProducer
{
    public List<(string Topic, string Key, object Envelope)> Published { get; } = [];

    public Task<PublishResult> PublishAsync<TPayload>(
        string topic, string partitionKey, EventEnvelope<TPayload> envelope, CancellationToken cancellationToken = default)
    {
        Published.Add((topic, partitionKey, envelope!));
        return Task.FromResult(new PublishResult(topic, 0, Published.Count));
    }

    public Task<IReadOnlyList<PublishResult>> PublishBatchAsync<TPayload>(
        string topic, IReadOnlyList<KeyedEvent<TPayload>> messages, CancellationToken cancellationToken = default)
    {
        var results = new List<PublishResult>();

        foreach (var (key, envelope) in messages)
        {
            Published.Add((topic, key, envelope!));
            results.Add(new PublishResult(topic, 0, Published.Count));
        }

        return Task.FromResult<IReadOnlyList<PublishResult>>(results);
    }


    public Task<PublishResult> PublishRawAsync(
        string topic, string partitionKey, byte[] payload,
        IReadOnlyDictionary<string, string>? headers = null, CancellationToken cancellationToken = default)
    {
        Published.Add((topic, partitionKey, System.Text.Encoding.UTF8.GetString(payload)));
        return Task.FromResult(new PublishResult(topic, 0, Published.Count));
    }

    public void Flush(TimeSpan timeout) { }
}
