using IncidentIQ.Domain.Abstractions;
using IncidentIQ.Domain.Enums;

namespace IncidentIQ.Domain.Entities;

/// <summary>
/// A specific version of a <see cref="MonitoredService"/> released into a
/// specific <see cref="Environment"/> at a known time.
///
/// This is the single most valuable piece of correlation data IncidentIQ has:
/// most incidents start minutes after a deployment, so "what shipped just
/// before this started?" answers a large share of investigations on its own.
/// It is why <see cref="Incident.SuspectedDeploymentId"/> exists.
/// </summary>
public class Deployment : ITenantScoped, ICreatedAt
{
    public Guid Id { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid MonitoredServiceId { get; set; }
    public Guid EnvironmentId { get; set; }

    /// <summary>Release identifier, e.g. "2.31.0". Not unique - rollbacks redeploy.</summary>
    public string Version { get; set; } = null!;

    public string? CommitSha { get; set; }
    public string? DeployedBy { get; set; }

    /// <summary>When the release went live, which is what incidents correlate against.</summary>
    public DateTimeOffset DeployedAt { get; set; }

    public DeploymentStatus Status { get; set; } = DeploymentStatus.Succeeded;

    /// <summary>Open-ended CI metadata: pipeline URL, PR number, changed files.</summary>
    public string? Metadata { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public MonitoredService MonitoredService { get; set; } = null!;
    public Environment Environment { get; set; } = null!;
}
