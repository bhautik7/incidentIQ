namespace IncidentIQ.Outbox;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>
    /// How often the publisher looks for work.
    ///
    /// This is the added latency between committing a domain change and the
    /// event reaching Kafka. Change data capture would remove it entirely, at
    /// the cost of running Kafka Connect and managing logical replication -
    /// not a trade worth making at incident volume, which is hundreds of events
    /// a day rather than millions.
    /// </summary>
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>
    /// Rows claimed per pass. Bounds how long one transaction holds its locks,
    /// which matters because the publish happens inside that transaction.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Attempts before a row is dead-lettered and stops being retried.</summary>
    public int MaxAttempts { get; set; } = 10;

    /// <summary>First backoff delay; doubles each attempt up to the cap.</summary>
    public int RetryBaseDelayMs { get; set; } = 500;

    public int RetryMaxDelaySeconds { get; set; } = 300;

    /// <summary>
    /// How long published rows are kept before the janitor deletes them.
    /// Long enough to answer "was this event actually sent?" during an
    /// investigation, short enough that the table does not grow without bound.
    /// </summary>
    public int RetentionHours { get; set; } = 168;

    public int JanitorIntervalMinutes { get; set; } = 60;

    /// <summary>Set false in hosts that write to the outbox but should not drain it.</summary>
    public bool Enabled { get; set; } = true;
}
