using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IncidentIQ.Messaging;

public sealed class KafkaConsumerSubscription<TPayload>
{
    public required string Topic { get; init; }
    public required string ConsumerGroup { get; init; }

    /// <summary>
    /// Where messages go when they cannot be handled. Null means a failing
    /// message blocks the partition rather than being dropped - correct for a
    /// stream where losing an event is worse than stalling.
    /// </summary>
    public string? DeadLetterTopic { get; init; } = Topics.LogsFailed;

    /// <summary>
    /// Give every process its own consumer group, so each one receives every
    /// message rather than sharing the partitions out between them.
    ///
    /// The default - a shared group - is right for work: two replicas of the
    /// event processor should split the load, and a message handled once is
    /// handled. It is exactly wrong for a fan-out, where each replica holds
    /// different client connections and needs the same message the others got.
    /// Sharing a group there means an incident reaches whichever replica drew
    /// the partition and nobody connected to the others ever hears about it.
    /// </summary>
    public bool BroadcastToEveryInstance { get; init; }

    /// <summary>
    /// Where a brand new group starts. Null uses the configured default.
    ///
    /// A fan-out wants "latest": its groups are new on every start, and
    /// "earliest" would replay the entire retained history into connected
    /// clients as though it had just happened.
    /// </summary>
    public string? AutoOffsetResetOverride { get; init; }

    /// <summary>
    /// One suffix per process: stable for its lifetime, different between
    /// processes, which is exactly the shape a broadcast group needs.
    /// </summary>
    private static readonly string InstanceSuffix = Guid.NewGuid().ToString("N")[..8];

    /// <summary>
    /// The group this subscription actually joins.
    ///
    /// A restarted process joins under a new name and leaves its previous group
    /// empty; Kafka expires those on its own once offsets age out. That is the
    /// accepted cost of broadcast semantics without a backplane, and it is why
    /// this is opt-in rather than the default.
    /// </summary>
    public string ResolvedConsumerGroup =>
        BroadcastToEveryInstance ? $"{ConsumerGroup}-{InstanceSuffix}" : ConsumerGroup;
}

/// <summary>
/// The consume loop: subscribe, deserialise, hand off, retry, dead-letter,
/// commit, and shut down cleanly. One instance per topic per process.
///
/// Two decisions shape everything here.
///
/// <b>Offsets are committed manually, after the work.</b> An offset means "this
/// message is done", so committing it before the handler has succeeded converts
/// at-least-once into at-most-once and silently loses data on the next crash.
///
/// <b>Shutdown is cooperative.</b> On stop, the loop finishes the message in
/// flight, commits what it has, and calls Close() to leave the consumer group
/// deliberately. Skipping that turns every deployment into a session-timeout
/// wait plus an avoidable rebalance.
/// </summary>
public sealed class KafkaConsumerService<TPayload, THandler>(
    KafkaConsumerSubscription<TPayload> subscription,
    IOptions<KafkaOptions> kafkaOptions,
    IServiceScopeFactory scopeFactory,
    IEventProducer producer,
    ConsumerLivenessRegistry liveness,
    ILogger<KafkaConsumerService<TPayload, THandler>> logger) : BackgroundService
    where THandler : IEventHandler<TPayload>
{
    private readonly KafkaOptions _kafka = kafkaOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Consume() blocks. Yield first so host startup is not held up by this
        // service, then run the loop on our own thread-pool thread.
        await Task.Yield();

        var consumerOptions = _kafka.Consumer;

        var config = new ConsumerConfig
        {
            BootstrapServers = _kafka.BootstrapServers,
            GroupId = subscription.ResolvedConsumerGroup,
            ClientId = $"{_kafka.ClientId}-{subscription.ResolvedConsumerGroup}",

            // Both false. Auto-commit acknowledges on a timer; auto-store marks
            // a message done the moment it is handed to us, before the handler
            // has run. Offsets are stored explicitly after success instead.
            EnableAutoCommit = consumerOptions.EnableAutoCommit,
            EnableAutoOffsetStore = false,

            AutoOffsetReset = Enum.Parse<AutoOffsetReset>(
                subscription.AutoOffsetResetOverride ?? consumerOptions.AutoOffsetReset, ignoreCase: true),
            MaxPollIntervalMs = consumerOptions.MaxPollIntervalMs,
            SessionTimeoutMs = consumerOptions.SessionTimeoutMs,
            EnablePartitionEof = false
        };

        using var consumer = new ConsumerBuilder<string, byte[]>(config)
            .SetErrorHandler((_, error) => logger.LogWarning(
                "Kafka consumer error on {Topic}: {Reason} (fatal={Fatal})",
                subscription.Topic, error.Reason, error.IsFatal))
            .SetPartitionsAssignedHandler((_, partitions) => logger.LogInformation(
                "Group {Group} assigned {Count} partition(s) of {Topic}: [{Partitions}]",
                subscription.ResolvedConsumerGroup, partitions.Count, subscription.Topic,
                string.Join(", ", partitions.Select(p => p.Partition.Value))))
            .SetPartitionsRevokedHandler((_, partitions) => logger.LogInformation(
                "Group {Group} revoked {Count} partition(s) of {Topic}",
                subscription.ResolvedConsumerGroup, partitions.Count, subscription.Topic))
            .Build();

        consumer.Subscribe(subscription.Topic);

        // Registered before the first poll so a loop that dies immediately is
        // still visible as a consumer that stopped, rather than as one that
        // never existed.
        liveness.AllowPollInterval(TimeSpan.FromMilliseconds(consumerOptions.MaxPollIntervalMs));
        liveness.Register(subscription.Topic, subscription.ResolvedConsumerGroup);

        logger.LogInformation(
            "Consuming {Topic} as group {Group}. Manual offset commits every {Interval}ms or {Count} messages.",
            subscription.Topic, subscription.ResolvedConsumerGroup,
            consumerOptions.CommitIntervalMs, consumerOptions.CommitEveryMessages);

        var sinceLastCommit = 0;
        var commitTimer = Stopwatch.StartNew();

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Reported before the poll, not after: a Consume() that
                // blocks for its full timeout is normal, and waiting until it
                // returns would make an idle topic look like a stall.
                liveness.ReportPoll(subscription.Topic, subscription.ResolvedConsumerGroup, consumer.Assignment.Count);

                ConsumeResult<string, byte[]>? result;

                try
                {
                    // Short timeout so cancellation is noticed promptly even
                    // when the topic is idle.
                    result = consumer.Consume(TimeSpan.FromMilliseconds(500));
                }
                catch (ConsumeException ex)
                {
                    logger.LogError(ex, "Consume failed on {Topic}: {Reason}", subscription.Topic, ex.Error.Reason);
                    continue;
                }

                if (result is null)
                {
                    // Idle: still commit anything stored, so a quiet topic does
                    // not sit on uncommitted offsets indefinitely.
                    if (sinceLastCommit > 0 && commitTimer.ElapsedMilliseconds >= consumerOptions.CommitIntervalMs)
                    {
                        CommitStoredOffsets(consumer, ref sinceLastCommit, commitTimer);
                    }

                    continue;
                }

                await ProcessAsync(consumer, result, stoppingToken);

                sinceLastCommit++;

                if (sinceLastCommit >= consumerOptions.CommitEveryMessages
                    || commitTimer.ElapsedMilliseconds >= consumerOptions.CommitIntervalMs)
                {
                    CommitStoredOffsets(consumer, ref sinceLastCommit, commitTimer);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            if (sinceLastCommit > 0)
            {
                CommitStoredOffsets(consumer, ref sinceLastCommit, commitTimer);
            }

            // Leaves the group immediately instead of waiting for the session to
            // time out, so the next replica picks up these partitions in
            // seconds rather than tens of seconds.
            consumer.Close();

            // Deregistered so a container that is deliberately shutting down
            // does not report its own stopped consumer as a fault.
            liveness.Deregister(subscription.Topic, subscription.ResolvedConsumerGroup);

            logger.LogInformation("Consumer for {Topic} closed cleanly.", subscription.Topic);
        }
    }

    private async Task ProcessAsync(
        IConsumer<string, byte[]> consumer,
        ConsumeResult<string, byte[]> result,
        CancellationToken cancellationToken)
    {
        var options = _kafka.Consumer;

        for (var attempt = 1; attempt <= options.MaxRetryAttempts; attempt++)
        {
            try
            {
                var envelope = EventJson.Deserialize<TPayload>(result.Message.Value);

                var context = new EventContext(
                    result.Topic,
                    result.Partition.Value,
                    result.Offset.Value,
                    result.Message.Key,
                    result.Message.Timestamp.UtcDateTime,
                    attempt);

                // A fresh scope per message: handlers get their own DbContext
                // and their own tenant context, exactly like a web request.
                using var scope = scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<THandler>();

                await handler.HandleAsync(envelope, context, cancellationToken);

                // Only now is the message "done". StoreOffset records offset+1,
                // which is where a restart resumes from.
                consumer.StoreOffset(result);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down mid-message. Do NOT store the offset: this
                // message has not been handled, and must be redelivered.
                throw;
            }
            catch (PermanentEventException ex)
            {
                logger.LogError(ex,
                    "Permanent failure on {Topic}[{Partition}]@{Offset}; dead-lettering without retry.",
                    result.Topic, result.Partition.Value, result.Offset.Value);

                await DeadLetterAsync(consumer, result, ex, attempt, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < options.MaxRetryAttempts)
            {
                // Exponential backoff with jitter. Without jitter, several
                // replicas recovering from one PostgreSQL restart retry in
                // lockstep and knock it over again.
                var delay = TimeSpan.FromMilliseconds(
                    options.RetryBaseDelayMs * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 100));

                logger.LogWarning(ex,
                    "Attempt {Attempt}/{Max} failed on {Topic}[{Partition}]@{Offset}; retrying in {Delay}ms.",
                    attempt, options.MaxRetryAttempts, result.Topic, result.Partition.Value, result.Offset.Value,
                    (int)delay.TotalMilliseconds);

                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "All {Max} attempts failed on {Topic}[{Partition}]@{Offset}; dead-lettering.",
                    options.MaxRetryAttempts, result.Topic, result.Partition.Value, result.Offset.Value);

                await DeadLetterAsync(consumer, result, ex, attempt, cancellationToken);
                return;
            }
        }
    }

    private async Task DeadLetterAsync(
        IConsumer<string, byte[]> consumer,
        ConsumeResult<string, byte[]> result,
        Exception exception,
        int attempts,
        CancellationToken cancellationToken)
    {
        if (subscription.DeadLetterTopic is null)
        {
            // No dead-letter topic configured: leave the offset unstored so the
            // message is redelivered. The partition stalls, loudly, rather than
            // quietly dropping an event.
            logger.LogCritical(
                "No dead-letter topic for {Topic}; partition {Partition} will stall at offset {Offset}.",
                result.Topic, result.Partition.Value, result.Offset.Value);
            return;
        }

        var original = Encoding.UTF8.GetString(result.Message.Value);

        // The original bytes are preserved verbatim so a replay is
        // byte-identical; everything else is diagnostics.
        var failure = new LogFailed
        {
            SourceTopic = result.Topic,
            SourcePartition = result.Partition.Value,
            SourceOffset = result.Offset.Value,
            Reason = exception.Message,
            ExceptionType = exception.GetType().FullName,
            Attempts = attempts,
            FailedAt = DateTimeOffset.UtcNow,
            OriginalPayload = original
        };

        var tenantId = TryReadTenantId(result) ?? Guid.Empty;

        var envelope = EventEnvelope<LogFailed>.Create(
            EventTypes.LogFailed,
            tenantId,
            failure,
            correlationId: TryReadCorrelationId(result));

        try
        {
            await producer.PublishAsync(
                subscription.DeadLetterTopic,
                result.Message.Key ?? tenantId.ToString(),
                envelope,
                cancellationToken);

            // Dead-lettered successfully, so this message is accounted for and
            // the partition may move on.
            consumer.StoreOffset(result);
        }
        catch (Exception ex)
        {
            // If even the DLQ write fails, do not store the offset. Redelivery
            // is far better than a silently dropped event.
            logger.LogCritical(ex,
                "Failed to dead-letter {Topic}[{Partition}]@{Offset}; it will be redelivered.",
                result.Topic, result.Partition.Value, result.Offset.Value);
        }
    }

    private void CommitStoredOffsets(
        IConsumer<string, byte[]> consumer,
        ref int sinceLastCommit,
        Stopwatch commitTimer)
    {
        try
        {
            var committed = consumer.Commit();

            if (committed.Count > 0)
            {
                logger.LogDebug("Committed {Count} offset(s) for {Topic}: [{Offsets}]",
                    committed.Count, subscription.Topic,
                    string.Join(", ", committed.Select(o => $"{o.Partition.Value}:{o.Offset.Value}")));
            }
        }
        catch (KafkaException ex) when (ex.Error.Code == ErrorCode.Local_NoOffset)
        {
            // Nothing stored yet; harmless.
        }
        catch (KafkaException ex)
        {
            // The offsets stay stored and the next commit picks them up. Worst
            // case some messages are reprocessed, which idempotent handlers absorb.
            logger.LogError(ex, "Offset commit failed for {Topic}: {Reason}", subscription.Topic, ex.Error.Reason);
        }
        finally
        {
            sinceLastCommit = 0;
            commitTimer.Restart();
        }
    }

    private static Guid? TryReadTenantId(ConsumeResult<string, byte[]> result) =>
        TryReadGuidHeader(result, EventHeaders.TenantId);

    private static Guid? TryReadCorrelationId(ConsumeResult<string, byte[]> result) =>
        TryReadGuidHeader(result, EventHeaders.CorrelationId);

    private static Guid? TryReadGuidHeader(ConsumeResult<string, byte[]> result, string name)
    {
        if (result.Message.Headers is null
            || !result.Message.Headers.TryGetLastBytes(name, out var bytes))
        {
            return null;
        }

        return Guid.TryParse(Encoding.UTF8.GetString(bytes), out var value) ? value : null;
    }
}
