using IncidentIQ.Shared.Auth;
using Microsoft.AspNetCore.SignalR;

namespace IncidentIQ.Api.Realtime;

/// <summary>
/// The names clients subscribe under, and the methods they receive.
///
/// Constants rather than inline strings because both ends of this contract are
/// in different languages and a typo is a message that is simply never
/// delivered - no error, no log, nothing to notice.
/// </summary>
public static class RealtimeEvents
{
    public const string IncidentDetected = "incidentDetected";
    public const string IncidentChanged = "incidentChanged";
    public const string AnalysisCompleted = "analysisCompleted";

    /// <summary>
    /// The SignalR group for one organization.
    ///
    /// Every broadcast goes to one of these and never to all clients. The read
    /// API leans on EF's global query filters for isolation, but a hub has no
    /// query to filter - the server chooses who to push to, so the tenant has
    /// to be in the group name or there is nothing enforcing it at all.
    /// </summary>
    public static string GroupFor(Guid organizationId) => $"org:{organizationId}";
}

/// <summary>
/// The dashboard's live connection.
///
/// Push-only. Clients receive events and never invoke anything, so there are no
/// hub methods to guard: a connection's entire authority is which group it was
/// placed in, and it does not choose that - the server does, from the API key.
///
/// What is pushed is deliberately thin. An event says an incident changed and
/// carries just enough to decide whether the current view cares; the client
/// then refetches through the same typed endpoints it already uses. Pushing
/// whole entities would mean two code paths producing the same screen, and the
/// one that only runs during a live update is the one that silently rots.
/// </summary>
public sealed class IncidentHub(ILogger<IncidentHub> logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var tenant = Context.GetHttpContext()?.GetTenantContext();

        if (tenant is null)
        {
            // Should be unreachable: the hub path is guarded. Aborting rather
            // than leaving the connection open matters anyway, because a
            // connection in no group is one that silently receives nothing.
            logger.LogWarning("Rejected a hub connection that carried no tenant.");
            Context.Abort();
            return;
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, RealtimeEvents.GroupFor(tenant.TenantId));

        logger.LogDebug(
            "Hub connection {ConnectionId} joined organization {TenantId}.",
            Context.ConnectionId, tenant.TenantId);

        await base.OnConnectedAsync();
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // Group membership is cleaned up by SignalR on disconnect; this exists
        // to make an unexpected drop visible rather than silent.
        if (exception is not null)
        {
            logger.LogDebug(exception, "Hub connection {ConnectionId} dropped.", Context.ConnectionId);
        }

        return base.OnDisconnectedAsync(exception);
    }
}
