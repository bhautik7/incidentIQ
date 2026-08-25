namespace IncidentIQ.Ingestion;

/// <summary>
/// Every limit the ingestion endpoint enforces, in one place and configurable
/// per environment.
///
/// These are not arbitrary. Each one exists because without it a single client
/// - usually by accident, occasionally not - can degrade the service for
/// everyone else.
/// </summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    /// <summary>
    /// Events per request. Bounds the work one request can create, bounds the
    /// memory a request can pin, and keeps p99 latency predictable. Clients
    /// that want more throughput send more requests, which the rate limiter
    /// can then price fairly.
    /// </summary>
    public int MaxBatchSize { get; set; } = 500;

    /// <summary>
    /// Byte ceiling, enforced before the body is read. MaxBatchSize alone is
    /// not enough: 500 events each carrying a 10 MB stack trace is a small
    /// batch and a very large request.
    /// </summary>
    public long MaxRequestBodyBytes { get; set; } = 5 * 1024 * 1024;

    public int MaxServiceNameLength { get; set; } = 100;
    public int MaxEnvironmentNameLength { get; set; } = 50;
    public int MaxMessageLength { get; set; } = 8_000;
    public int MaxExceptionTypeLength { get; set; } = 500;
    public int MaxStackTraceLength { get; set; } = 32_000;
    public int MaxTraceIdLength { get; set; } = 64;
    public int MaxSpanIdLength { get; set; } = 32;
    public int MaxHostLength { get; set; } = 255;

    public int MaxMetadataEntries { get; set; } = 50;
    public int MaxMetadataKeyLength { get; set; } = 128;
    public int MaxMetadataValueLength { get; set; } = 1_024;

    /// <summary>
    /// How far ahead of server time an event may be dated. Client clocks drift;
    /// rejecting anything in the future at all would drop legitimate logs.
    /// </summary>
    public TimeSpan MaxClockSkewAhead { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// How far back an event may be dated. Matches Kafka retention: an event
    /// older than the replay window cannot be usefully processed, and accepting
    /// it would silently distort incident timelines.
    /// </summary>
    public TimeSpan MaxEventAge { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Requests per tenant per replenishment period before 429.</summary>
    public int RateLimitTokensPerPeriod { get; set; } = 1_000;

    /// <summary>Burst allowance. Larger than the steady rate so a client can catch up after a blip.</summary>
    public int RateLimitBucketCapacity { get; set; } = 2_000;

    public int RateLimitPeriodSeconds { get; set; } = 1;
}
