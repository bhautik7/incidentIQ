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
    /// <summary>Opened by a detection rule. Nobody has looked at it yet.</summary>
    Detected,

    /// <summary>Someone has taken it. Still active, still counting occurrences.</summary>
    Investigating,

    /// <summary>Fixed. Resolution notes feed future similarity search.</summary>
    Resolved,

    /// <summary>Known and deliberately not worth acting on. Occurrences stop opening incidents.</summary>
    Ignored
}

/// <summary>
/// Which rule opened an incident. Recorded so a noisy rule can be found and
/// tuned - without it, "why did this open?" is unanswerable after the fact.
/// </summary>
public enum DetectionRule
{
    /// <summary>Occurrences in the window crossed an absolute threshold.</summary>
    CountThreshold,

    /// <summary>The current rate is far above this pattern's own recent baseline.</summary>
    RateSpike,

    /// <summary>A burst of 5xx responses across a service, spanning fingerprints.</summary>
    ServerErrorSpike,

    /// <summary>A fingerprint never seen before appeared just after a deployment.</summary>
    NewErrorAfterDeployment,

    /// <summary>Opened by a person rather than by a rule.</summary>
    Manual
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
    InvestigationStarted,
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
