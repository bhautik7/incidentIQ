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

public sealed class KafkaBatchConsumerSubscription<TPayload>
{
    public required string Topic { get; init; }
    public required string ConsumerGroup { get; init; }
    public string? DeadLetterTopic { get; init; } = Topics.LogsFailed;

    /// <summary>Maximum events handed to the handler at once.</summary>
    public int MaxBatchSize { get; init; } = 500;

    /// <summary>
    /// How long to wait for a batch to fill before processing what has
    /// arrived. Without this, a quiet topic would sit on a half-full batch
    /// indefinitely and latency would be unbounded.
    /// </summary>
    public int MaxBatchWaitMs { get; init; } = 250;
}

/// <summary>
/// Accumulates messages into batches, hands each batch to a handler, and
/// commits offsets only after the handler succeeds.
///
/// <b>Poison-message isolation.</b> A batch that keeps failing is the hard case:
/// one malformed message must not cost the other 499, and it must not stall the
/// partition forever either. After the batch-level retries are exhausted, the
/// batch is replayed one message at a time. Whatever succeeds is applied,
/// whatever fails is dead-lettered individually, and the partition moves on.
/// That converts "somewhere in these 500 there is a bad one" into an exact
/// answer without ever dropping a good message.
///
/// <b>Offsets.</b> Stored per partition after the batch is applied, committed on
/// an interval and again on shutdown. Nothing is ever acknowledged before the
/// work it represents is durable.
/// </summary>
public sealed class KafkaBatchConsumerService<TPayload, THandler>(
    KafkaBatchConsumerSubscription<TPayload> subscription,
    IOptions<KafkaOptions> kafkaOptions,
    IServiceScopeFactory scopeFactory,
    IEventProducer producer,
    ConsumerLivenessRegistry liveness,
    ILogger<KafkaBatchConsumerService<TPayload, THandler>> logger) : BackgroundService
    where THandler : IEventBatchHandler<TPayload>
{
    private readonly KafkaOptions _kafka = kafkaOptions.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Consume() blocks. Yield first so host startup is not held up.
        await Task.Yield();

        var consumerOptions = _kafka.Consumer;

        var config = new ConsumerConfig
        {
            BootstrapServers = _kafka.BootstrapServers,
            GroupId = subscription.ConsumerGroup,
            ClientId = $"{_kafka.ClientId}-{subscription.ConsumerGroup}",

            // Both false. Auto-commit acknowledges on a timer; auto-store marks
            // a message done the moment it is handed to us, before the handler
            // has run. Offsets are stored explicitly after success instead.
            EnableAutoCommit = false,
            EnableAutoOffsetStore = false,

            AutoOffsetReset = Enum.Parse<AutoOffsetReset>(consumerOptions.AutoOffsetReset, ignoreCase: true),
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
                subscription.ConsumerGroup, partitions.Count, subscription.Topic,
                string.Join(", ", partitions.Select(p => p.Partition.Value))))
            .SetPartitionsRevokedHandler((_, partitions) => logger.LogInformation(
                "Group {Group} revoked {Count} partition(s) of {Topic}",
                subscription.ConsumerGroup, partitions.Count, subscription.Topic))
            .Build();

        consumer.Subscribe(subscription.Topic);

        // Registered before the first poll so a loop that dies immediately is
        // still visible as a consumer that stopped, rather than one that never
        // existed.
        liveness.AllowPollInterval(TimeSpan.FromMilliseconds(consumerOptions.MaxPollIntervalMs));
        liveness.Register(subscription.Topic, subscription.ConsumerGroup);

        logger.LogInformation(
            "Batch-consuming {Topic} as group {Group}. batchSize={BatchSize} maxWaitMs={MaxWait}",
            subscription.Topic, subscription.ConsumerGroup,
            subscription.MaxBatchSize, subscription.MaxBatchWaitMs);

        var commitTimer = Stopwatch.StartNew();
        var uncommitted = 0;

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Reported per batch cycle rather than per inner poll: one
                // cycle is bounded by MaxBatchWaitMs, so this still advances
                // several times a second on an idle topic.
                liveness.ReportPoll(subscription.Topic, subscription.ConsumerGroup, consumer.Assignment.Count);

                var raw = CollectBatch(consumer, stoppingToken);

                if (raw.Count == 0)
                {
                    if (uncommitted > 0 && commitTimer.ElapsedMilliseconds >= consumerOptions.CommitIntervalMs)
                    {
                        CommitStoredOffsets(consumer, ref uncommitted, commitTimer);
                    }

                    continue;
                }

                await ProcessBatchAsync(consumer, raw, stoppingToken);

                uncommitted += raw.Count;

                if (uncommitted >= consumerOptions.CommitEveryMessages
                    || commitTimer.ElapsedMilliseconds >= consumerOptions.CommitIntervalMs)
                {
                    CommitStoredOffsets(consumer, ref uncommitted, commitTimer);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        finally
        {
            if (uncommitted > 0)
            {
                CommitStoredOffsets(consumer, ref uncommitted, commitTimer);
            }

            // Leave the group deliberately rather than waiting for the session
            // to time out, so a replacement replica picks up these partitions
            // in seconds.
            consumer.Close();

            // Deregistered so a container that is deliberately shutting down
            // does not report its own stopped consumer as a fault.
            liveness.Deregister(subscription.Topic, subscription.ConsumerGroup);
            logger.LogInformation("Batch consumer for {Topic} closed cleanly.", subscription.Topic);
        }
    }

    /// <summary>
    /// Fills a batch, stopping at the size limit, the time limit, or shutdown.
    /// </summary>
    private List<ConsumeResult<string, byte[]>> CollectBatch(
        IConsumer<string, byte[]> consumer,
        CancellationToken stoppingToken)
    {
        var batch = new List<ConsumeResult<string, byte[]>>(subscription.MaxBatchSize);
        var deadline = Stopwatch.StartNew();

        while (batch.Count < subscription.MaxBatchSize
               && deadline.ElapsedMilliseconds < subscription.MaxBatchWaitMs
               && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Short poll so shutdown and the batch deadline are both noticed
                // promptly on an idle topic.
                var result = consumer.Consume(TimeSpan.FromMilliseconds(50));

                if (result is null)
                {
                    // Nothing waiting. If we already have work, take it rather
                    // than holding it for a batch that may never fill.
                    if (batch.Count > 0)
                    {
                        break;
                    }

                    continue;
                }

                batch.Add(result);
            }
            catch (ConsumeException ex)
            {
                logger.LogError(ex, "Consume failed on {Topic}: {Reason}", subscription.Topic, ex.Error.Reason);
                break;
            }
        }

        return batch;
    }

    private async Task ProcessBatchAsync(
        IConsumer<string, byte[]> consumer,
        List<ConsumeResult<string, byte[]>> raw,
        CancellationToken cancellationToken)
    {
        var options = _kafka.Consumer;

        // Messages that cannot even be deserialised can never succeed, so they
        // are removed here rather than being allowed to poison every retry of
        // the batch they happen to land in.
        var items = new List<EventBatchItem<TPayload>>(raw.Count);
        var decoded = new List<ConsumeResult<string, byte[]>>(raw.Count);

        foreach (var result in raw)
        {
            if (TryDeserialize(result, out var item))
            {
                items.Add(item);
                decoded.Add(result);
            }
            else
            {
                await DeadLetterAsync(consumer, result,
                    new PermanentEventException("Message body is not a valid event envelope."),
                    attempts: 1, cancellationToken);
            }
        }

        if (items.Count == 0)
        {
            return;
        }

        for (var attempt = 1; attempt <= options.MaxRetryAttempts; attempt++)
        {
            try
            {
                await InvokeHandlerAsync(items, cancellationToken);
                StoreOffsets(consumer, decoded);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // Shutting down mid-batch. Store nothing: these messages have
                // not been handled and must be redelivered.
                throw;
            }
            catch (PermanentEventException ex)
            {
                // Something in this batch can never succeed. Retrying the whole
                // batch would burn three attempts and two backoff delays to
                // reach the same conclusion, so go straight to isolation, which
                // applies the good messages and dead-letters only the bad one.
                logger.LogWarning(ex,
                    "Permanent failure in a batch of {Count} on {Topic}; isolating without retrying.",
                    items.Count, subscription.Topic);

                await IsolateAsync(consumer, items, decoded, cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < options.MaxRetryAttempts)
            {
                // Jitter matters: without it, several replicas recovering from
                // one PostgreSQL restart retry in lockstep and knock it over again.
                var delay = TimeSpan.FromMilliseconds(
                    options.RetryBaseDelayMs * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 100));

                logger.LogWarning(ex,
                    "Batch attempt {Attempt}/{Max} failed for {Count} message(s) on {Topic}; retrying in {Delay}ms.",
                    attempt, options.MaxRetryAttempts, items.Count, subscription.Topic, (int)delay.TotalMilliseconds);

                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "Batch failed after {Max} attempts on {Topic}; isolating the poison message(s) "
                    + "by replaying {Count} message(s) individually.",
                    options.MaxRetryAttempts, subscription.Topic, items.Count);

                await IsolateAsync(consumer, items, decoded, cancellationToken);
                return;
            }
        }
    }

    /// <summary>
    /// Replays a failed batch one message at a time.
    ///
    /// Slow by design and rare by construction: it only runs after a batch has
    /// already failed every retry. The payoff is that a single bad message
    /// costs one dead letter instead of 500, and the partition keeps moving.
    /// </summary>
    private async Task IsolateAsync(
        IConsumer<string, byte[]> consumer,
        List<EventBatchItem<TPayload>> items,
        List<ConsumeResult<string, byte[]>> decoded,
        CancellationToken cancellationToken)
    {
        var poisoned = 0;

        for (var i = 0; i < items.Count; i++)
        {
            try
            {
                await InvokeHandlerAsync([items[i]], cancellationToken);
                StoreOffsets(consumer, [decoded[i]]);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                poisoned++;
                await DeadLetterAsync(consumer, decoded[i], ex, _kafka.Consumer.MaxRetryAttempts, cancellationToken);
            }
        }

        logger.LogWarning(
            "Isolation complete on {Topic}: {Poisoned} of {Total} message(s) dead-lettered, the rest applied.",
            subscription.Topic, poisoned, items.Count);
    }

    private async Task InvokeHandlerAsync(
        IReadOnlyList<EventBatchItem<TPayload>> items,
        CancellationToken cancellationToken)
    {
        // A fresh scope per batch: the handler gets its own DbContext and its
        // own unit of work, exactly like a web request.
        using var scope = scopeFactory.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<THandler>();

        await handler.HandleBatchAsync(items, cancellationToken);
    }

    private bool TryDeserialize(
        ConsumeResult<string, byte[]> result,
        out EventBatchItem<TPayload> item)
    {
        try
        {
            var envelope = EventJson.Deserialize<TPayload>(result.Message.Value);

            item = new EventBatchItem<TPayload>(envelope, new EventContext(
                result.Topic,
                result.Partition.Value,
                result.Offset.Value,
                result.Message.Key,
                result.Message.Timestamp.UtcDateTime,
                Attempt: 1));

            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "Undeserialisable message on {Topic}[{Partition}]@{Offset}; dead-lettering.",
                result.Topic, result.Partition.Value, result.Offset.Value);

            item = null!;
            return false;
        }
    }

    private static void StoreOffsets(
        IConsumer<string, byte[]> consumer,
        IReadOnlyList<ConsumeResult<string, byte[]>> results)
    {
        // Only the highest offset per partition matters - Kafka offsets are a
        // watermark, not a set - so storing one per partition is both correct
        // and cheaper than storing every message.
        foreach (var highest in results
                     .GroupBy(r => r.Partition.Value)
                     .Select(g => g.MaxBy(r => r.Offset.Value)!))
        {
            consumer.StoreOffset(highest);
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
            // No dead-letter topic: leave the offset unstored so the message is
            // redelivered. The partition stalls loudly rather than quietly
            // dropping an event.
            logger.LogCritical(
                "No dead-letter topic for {Topic}; partition {Partition} will stall at offset {Offset}.",
                result.Topic, result.Partition.Value, result.Offset.Value);
            return;
        }

        var failure = new LogFailed
        {
            SourceTopic = result.Topic,
            SourcePartition = result.Partition.Value,
            SourceOffset = result.Offset.Value,
            Reason = exception.Message,
            ExceptionType = exception.GetType().FullName,
            Attempts = attempts,
            FailedAt = DateTimeOffset.UtcNow,
            // Verbatim, so a replay is byte-identical to the original.
            OriginalPayload = Encoding.UTF8.GetString(result.Message.Value)
        };

        var tenantId = TryReadGuidHeader(result, EventHeaders.TenantId) ?? Guid.Empty;

        var envelope = EventEnvelope<LogFailed>.Create(
            EventTypes.LogFailed,
            tenantId,
            failure,
            correlationId: TryReadGuidHeader(result, EventHeaders.CorrelationId));

        try
        {
            await producer.PublishAsync(
                subscription.DeadLetterTopic,
                result.Message.Key ?? tenantId.ToString(),
                envelope,
                cancellationToken);

            // Accounted for, so the partition may move past it.
            consumer.StoreOffset(result);
        }
        catch (Exception ex)
        {
            // If even the dead-letter write fails, do not store the offset.
            // Redelivery beats a silently dropped event.
            logger.LogCritical(ex,
                "Failed to dead-letter {Topic}[{Partition}]@{Offset}; it will be redelivered.",
                result.Topic, result.Partition.Value, result.Offset.Value);
        }
    }

    private void CommitStoredOffsets(
        IConsumer<string, byte[]> consumer,
        ref int uncommitted,
        Stopwatch commitTimer)
    {
        try
        {
            consumer.Commit();
        }
        catch (KafkaException ex) when (ex.Error.Code == ErrorCode.Local_NoOffset)
        {
            // Nothing stored yet; harmless.
        }
        catch (KafkaException ex)
        {
            // Offsets stay stored and the next commit picks them up. Worst case
            // some messages are reprocessed, which idempotent handlers absorb.
            logger.LogError(ex, "Offset commit failed for {Topic}: {Reason}", subscription.Topic, ex.Error.Reason);
        }
        finally
        {
            uncommitted = 0;
            commitTimer.Restart();
        }
    }

    private static Guid? TryReadGuidHeader(ConsumeResult<string, byte[]> result, string name)
    {
        if (result.Message.Headers is null || !result.Message.Headers.TryGetLastBytes(name, out var bytes))
        {
            return null;
        }

        return Guid.TryParse(Encoding.UTF8.GetString(bytes), out var value) ? value : null;
    }
}
