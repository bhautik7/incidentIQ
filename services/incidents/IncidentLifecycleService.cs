using System.Text.Json;
using IncidentIQ.Domain.Entities;
using IncidentIQ.Domain.Enums;
using IncidentIQ.Persistence;
using Microsoft.EntityFrameworkCore;

namespace IncidentIQ.Incidents;

/// <summary>
/// Raised when a transition is not legal from the incident's current state.
/// </summary>
public sealed class InvalidIncidentTransitionException(string message) : Exception(message);

/// <summary>
/// The incident lifecycle: Detected → Investigating → Resolved.
///
/// <code>
///                    ┌──────────────┐
///   rule fires  ───► │   Detected   │ ◄── Reopened (recurrence in cooldown)
///                    └──────┬───────┘
///          someone takes it │        └──────────────┐
///                           ▼                       │
///                    ┌───────────────┐              │
///                    │ Investigating │              │
///                    └──────┬────────┘              │
///                           │  fixed                │ not worth acting on
///                           ▼                       ▼
///                    ┌──────────────┐        ┌────────────┐
///                    │   Resolved   │        │  Ignored   │
///                    └──────────────┘        └────────────┘
/// </code>
///
/// Every transition writes an <see cref="IncidentEvent"/>. The status column is
/// the current state; the timeline is how it got there, and that history is
/// what an engineer joining a live incident actually reads.
///
/// Transitions are validated rather than assumed. Resolving an already-resolved
/// incident is almost always two people acting on stale UI, and silently
/// overwriting the first resolution loses who fixed it and how.
/// </summary>
public sealed class IncidentLifecycleService(IncidentIQDbContext dbContext, TimeProvider timeProvider)
{
    /// <summary>Statuses in which an incident still accrues occurrences.</summary>
    public static readonly IncidentStatus[] ActiveStatuses =
        [IncidentStatus.Detected, IncidentStatus.Investigating];

    public static bool IsActive(IncidentStatus status) => ActiveStatuses.Contains(status);

    /// <summary>Detected → Investigating. Someone has picked it up.</summary>
    public Task<Incident> StartInvestigatingAsync(
        Guid organizationId, Guid incidentId, Guid userId, CancellationToken cancellationToken = default) =>
        TransitionAsync(organizationId, incidentId,
            allowedFrom: [IncidentStatus.Detected],
            to: IncidentStatus.Investigating,
            eventType: IncidentEventType.InvestigationStarted,
            message: "Investigation started.",
            apply: (incident, now) =>
            {
                incident.InvestigationStartedAt = now;
                incident.InvestigatingUserId = userId;
            },
            actorUserId: userId,
            cancellationToken: cancellationToken);

    /// <summary>
    /// → Resolved. Allowed from Detected as well as Investigating: plenty of
    /// incidents are fixed by whoever spots them, without a formal handover.
    /// </summary>
    public Task<Incident> ResolveAsync(
        Guid organizationId, Guid incidentId, Guid userId, string? resolutionNotes,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(organizationId, incidentId,
            allowedFrom: [IncidentStatus.Detected, IncidentStatus.Investigating],
            to: IncidentStatus.Resolved,
            eventType: IncidentEventType.Resolved,
            message: string.IsNullOrWhiteSpace(resolutionNotes) ? "Resolved." : $"Resolved: {resolutionNotes}",
            apply: (incident, now) =>
            {
                incident.ResolvedAt = now;
                incident.ResolvedByUserId = userId;

                // Deliberately kept even when empty: resolution notes are the
                // single most useful thing the similarity search can offer the
                // next person to hit this.
                incident.ResolutionNotes = resolutionNotes;
            },
            actorUserId: userId,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Hands the incident to someone, without moving it along the lifecycle.
    ///
    /// Assignment is not a status: an incident can be Detected and owned, or
    /// Investigating and reassigned mid-flight. Keeping the two apart is what
    /// lets "who has this" be answered without also claiming work has started.
    ///
    /// Allowed only while the incident is still active - assigning a resolved
    /// incident to someone is asking them to do nothing.
    /// </summary>
    public Task<Incident> AssignAsync(
        Guid organizationId, Guid incidentId, Guid assigneeUserId, Guid actorUserId,
        string assigneeName, CancellationToken cancellationToken = default) =>
        TransitionAsync(organizationId, incidentId,
            allowedFrom: ActiveStatuses,
            // The status is deliberately unchanged; TransitionAsync writes the
            // event and the assignment, and "to" here is simply where it stays.
            to: null,
            eventType: IncidentEventType.Assigned,
            message: $"Assigned to {assigneeName}.",
            apply: (incident, _) => incident.InvestigatingUserId = assigneeUserId,
            actorUserId: actorUserId,
            cancellationToken: cancellationToken);

    /// <summary>
    /// A human note on the incident.
    ///
    /// Writes to the same timeline as the automated events rather than a
    /// separate comments table, because during an incident "what did the
    /// detector see" and "what did Ravi try" are read in one sequence, and
    /// splitting them means reconstructing the order by eye.
    /// </summary>
    public Task<Incident> AddNoteAsync(
        Guid organizationId, Guid incidentId, Guid actorUserId, string note,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(organizationId, incidentId,
            // A note is legal in any state. Writing down what actually fixed
            // something, after it is resolved, is a thing people do.
            allowedFrom: Enum.GetValues<IncidentStatus>(),
            to: null,
            eventType: IncidentEventType.Commented,
            message: note,
            apply: (_, _) => { },
            actorUserId: actorUserId,
            cancellationToken: cancellationToken);

    /// <summary>Resolved → Detected, by a person. The detector reopens automatically on recurrence.</summary>
    public Task<Incident> ReopenAsync(
        Guid organizationId, Guid incidentId, Guid userId, string reason,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(organizationId, incidentId,
            allowedFrom: [IncidentStatus.Resolved, IncidentStatus.Ignored],
            to: IncidentStatus.Detected,
            eventType: IncidentEventType.Reopened,
            message: $"Reopened: {reason}",
            apply: (incident, _) =>
            {
                incident.ResolvedAt = null;
                incident.ResolvedByUserId = null;
            },
            actorUserId: userId,
            cancellationToken: cancellationToken);

    /// <summary>
    /// → Ignored. Known, understood, not worth acting on.
    ///
    /// Distinct from Resolved: nothing was fixed. Keeping them apart matters
    /// because resolution notes feed the similarity search, and "we decided not
    /// to care" is not a fix anyone should be shown later.
    /// </summary>
    public Task<Incident> IgnoreAsync(
        Guid organizationId, Guid incidentId, Guid userId, string reason,
        CancellationToken cancellationToken = default) =>
        TransitionAsync(organizationId, incidentId,
            allowedFrom: [IncidentStatus.Detected, IncidentStatus.Investigating],
            to: IncidentStatus.Ignored,
            eventType: IncidentEventType.Ignored,
            message: $"Ignored: {reason}",
            apply: (_, _) => { },
            actorUserId: userId,
            cancellationToken: cancellationToken);

    /// <summary>
    /// Applies a change and records it, in one transaction.
    /// </summary>
    /// <param name="to">
    /// The status to move to, or null for a change that records itself on the
    /// timeline without advancing the lifecycle - an assignment or a note.
    /// </param>
    private async Task<Incident> TransitionAsync(
        Guid organizationId,
        Guid incidentId,
        IncidentStatus[] allowedFrom,
        IncidentStatus? to,
        IncidentEventType eventType,
        string message,
        Action<Incident, DateTimeOffset> apply,
        Guid actorUserId,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        return await dbContext.ExecuteInTransactionAsync(async () =>
        {
            var incident = await dbContext.Incidents
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(i => i.Id == incidentId && i.OrganizationId == organizationId, cancellationToken)
                ?? throw new InvalidIncidentTransitionException(
                    $"Incident {incidentId} does not exist in organization {organizationId}.");

            if (!allowedFrom.Contains(incident.Status))
            {
                throw new InvalidIncidentTransitionException(
                    to is null
                        ? $"Cannot record a {eventType} on incident {incidentId} while it is {incident.Status}. "
                          + $"Allowed while: {string.Join(", ", allowedFrom)}."
                        : $"Cannot move incident {incidentId} from {incident.Status} to {to}. "
                          + $"Allowed from: {string.Join(", ", allowedFrom)}.");
            }

            var from = incident.Status;
            if (to is not null)
            {
                incident.Status = to.Value;
            }

            apply(incident, now);

            dbContext.IncidentEvents.Add(new IncidentEvent
            {
                OrganizationId = organizationId,
                IncidentId = incidentId,
                Type = eventType,
                OccurredAt = now,
                ActorType = ActorType.User,
                ActorUserId = actorUserId,
                Message = message,
                // A status-neutral event records the state it happened in, so
                // the timeline still says where the incident stood at the time.
                Data = JsonSerializer.Serialize(
                    new { from = from.ToString(), to = (to ?? from).ToString() })
            });

            return incident;
        }, cancellationToken);
    }
}
