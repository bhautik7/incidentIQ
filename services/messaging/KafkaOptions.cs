namespace IncidentIQ.Messaging;

public sealed class KafkaOptions
{
    public const string SectionName = "Kafka";

    public string BootstrapServers { get; set; } = string.Empty;

    /// <summary>Prefixed to log lines and used as the Kafka client id, so a lagging consumer is traceable to a process.</summary>
    public string ClientId { get; set; } = "incidentiq";

    public KafkaProducerOptions Producer { get; set; } = new();
    public KafkaConsumerOptions Consumer { get; set; } = new();
}

public sealed class KafkaProducerOptions
{
    /// <summary>
    /// Exactly-once semantics into the broker: librdkafka's internal retries
    /// cannot produce a duplicate, and it will not silently reorder.
    /// </summary>
    public bool EnableIdempotence { get; set; } = true;

    /// <summary>
    /// "all": the leader waits for every in-sync replica. Anything weaker means
    /// a broker failure can lose an accepted log batch, which is the one thing
    /// ingestion promises not to do.
    /// </summary>
    public string Acks { get; set; } = "all";

    /// <summary>Wait briefly to fill a batch. Costs a few ms of latency, saves a large fraction of the request overhead.</summary>
    public int LingerMs { get; set; } = 20;

    /// <summary>Log text is highly repetitive, so compression pays for itself several times over.</summary>
    public string CompressionType { get; set; } = "lz4";

    public int MessageTimeoutMs { get; set; } = 30_000;
}

public sealed class KafkaConsumerOptions
{
    /// <summary>
    /// Always false. Auto-commit acknowledges messages on a timer rather than
    /// when the work is done, so a crash silently loses everything in flight.
    /// Offsets are stored after a handler succeeds and committed explicitly.
    /// </summary>
    public bool EnableAutoCommit { get; set; }

    /// <summary>
    /// "earliest": on a brand new group, start at the beginning. For a
    /// diagnostic tool, processing an old backlog beats a silent gap.
    /// </summary>
    public string AutoOffsetReset { get; set; } = "earliest";

    /// <summary>
    /// Must exceed the worst-case time to handle one batch, or the broker
    /// assumes the consumer is dead and rebalances mid-work.
    /// </summary>
    public int MaxPollIntervalMs { get; set; } = 300_000;

    public int SessionTimeoutMs { get; set; } = 45_000;

    /// <summary>How often stored offsets are committed. A crash can replay at most this much work.</summary>
    public int CommitIntervalMs { get; set; } = 5_000;

    /// <summary>Commit early if this many messages are handled before the interval elapses.</summary>
    public int CommitEveryMessages { get; set; } = 100;

    /// <summary>Attempts for a transient failure before the message is dead-lettered.</summary>
    public int MaxRetryAttempts { get; set; } = 3;

    /// <summary>First backoff delay; doubles each attempt, with jitter.</summary>
    public int RetryBaseDelayMs { get; set; } = 200;

    /// <summary>How long to wait for in-flight work to finish on shutdown before giving up.</summary>
    public int ShutdownTimeoutMs { get; set; } = 15_000;
}
