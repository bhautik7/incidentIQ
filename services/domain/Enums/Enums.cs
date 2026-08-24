namespace IncidentIQ.Domain.Enums;

// All of these are persisted as text, not as integers. The database is queried
// by hand constantly during incident triage, and `status = 'Open'` is readable
// where `status = 1` is a lookup in someone's head. The cost is a few bytes per
// row on tables where that does not matter; LogEvents.Level is the one hot-path
// exception and is still short enough to be free in practice.

public enum OrganizationStatus
{
    Active,
    Suspended
}

public enum UserStatus
{
    Invited,
    Active,
    Disabled
}

public enum DeploymentStatus
{
    InProgress,
    Succeeded,
    Failed,
    RolledBack
}

public enum LogEventLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Fatal
}

public enum IncidentStatus
{
    /// <summary>Newly detected, nobody has looked at it.</summary>
    Open,

    /// <summary>Someone has taken it; still active.</summary>
    Acknowledged,

    /// <summary>Fixed. Resolution notes feed future similarity search.</summary>
    Resolved,

    /// <summary>Known and deliberately not worth acting on.</summary>
    Ignored
}

public enum IncidentSeverity
{
    Low,
    Medium,
    High,
    Critical
}

public enum IncidentEventType
{
    Created,
    Escalated,
    SeverityChanged,
    Acknowledged,
    Assigned,
    Commented,
    AiAnalysisCompleted,
    Resolved,
    Reopened,
    Ignored
}

public enum ActorType
{
    /// <summary>The pipeline acted on its own; ActorUserId is null.</summary>
    System,
    User
}

public enum AiAnalysisStatus
{
    Pending,
    Completed,
    Failed
}
