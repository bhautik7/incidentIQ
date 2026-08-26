namespace IncidentIQ.Contracts;

/// <summary>
/// Topic names, in one place. A typo in a topic name is a message that
/// disappears silently, so these are never written as string literals.
/// </summary>
public static class Topics
{
    /// <summary>Raw log events exactly as accepted from a client. Key: tenant:service.</summary>
    public const string LogsRaw = "logs.raw";

    /// <summary>Log events after masking and fingerprinting. Key: tenant:service.</summary>
    public const string LogsNormalized = "logs.normalized";

    /// <summary>
    /// Log events that could not be processed. The dead-letter destination for
    /// the log path: original bytes preserved, diagnostics in headers, never
    /// replayed automatically.
    /// </summary>
    public const string LogsFailed = "logs.failed";

    /// <summary>Releases, so incidents can be correlated to what shipped. Key: tenant:service.</summary>
    public const string DeploymentsCreated = "deployments.created";

    /// <summary>A new incident was opened. Key: tenant:incident.</summary>
    public const string IncidentsDetected = "incidents.detected";

    /// <summary>Work request for the Python AI worker. Key: tenant:incident.</summary>
    public const string IncidentsAnalysisRequested = "incidents.analysis.requested";

    /// <summary>Result written back by the Python AI worker. Key: tenant:incident.</summary>
    public const string IncidentsAnalysisCompleted = "incidents.analysis.completed";

    public static readonly IReadOnlyList<string> All =
    [
        LogsRaw, LogsNormalized, LogsFailed, DeploymentsCreated,
        IncidentsDetected, IncidentsAnalysisRequested, IncidentsAnalysisCompleted
    ];
}

/// <summary>
/// Consumer group ids. One per logical job, never one per instance: members of
/// a group share the partitions, whereas a second group gets an independent
/// copy of the whole stream.
/// </summary>
public static class ConsumerGroups
{
    public const string IncidentProcessor = "incident-processor";
    public const string IncidentDetector = "incident-detector";
    public const string AiEnricher = "ai-enricher";
    public const string DeploymentCorrelator = "deployment-correlator";
}

/// <summary>
/// Values of <see cref="EventEnvelope{TPayload}.EventType"/>. Dotted and
/// lower-case, mirroring the topic they usually travel on.
/// </summary>
public static class EventTypes
{
    public const string LogReceived = "log.received";
    public const string LogNormalized = "log.normalized";
    public const string LogFailed = "log.failed";
    public const string DeploymentCreated = "deployment.created";
    public const string IncidentDetected = "incident.detected";
    public const string IncidentAnalysisRequested = "incident.analysis.requested";
    public const string IncidentAnalysisCompleted = "incident.analysis.completed";
}

/// <summary>
/// Kafka header names. Headers carry what a broker-side tool or a router needs
/// without paying to deserialise the body.
/// </summary>
public static class EventHeaders
{
    public const string EventId = "event-id";
    public const string EventType = "event-type";
    public const string EventVersion = "event-version";
    public const string TenantId = "tenant-id";
    public const string CorrelationId = "correlation-id";

    // Set only on messages routed to a dead-letter topic.
    public const string DeadLetterReason = "dlq-reason";
    public const string DeadLetterSourceTopic = "dlq-source-topic";
    public const string DeadLetterSourcePartition = "dlq-source-partition";
    public const string DeadLetterSourceOffset = "dlq-source-offset";
    public const string DeadLetterAttempts = "dlq-attempts";
    public const string DeadLetterFailedAt = "dlq-failed-at";
}
