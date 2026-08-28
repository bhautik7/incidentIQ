using System.Text.Json.Serialization;
using IncidentIQ.Contracts;
using IncidentIQ.Contracts.Payloads;
using IncidentIQ.Messaging;
using Microsoft.AspNetCore.SignalR;

namespace IncidentIQ.Api.Realtime;

/// <summary>
/// What a client is told when an incident opens.
///
/// Enough to decide whether the view being looked at cares, and no more. The
/// client refetches through the same typed endpoints it already uses rather
/// than trusting this as a source of truth - see <see cref="IncidentHub"/>.
/// </summary>
public sealed record IncidentDetectedNotification
{
    [JsonPropertyName("incidentId")]
    public required Guid IncidentId { get; init; }

    [JsonPropertyName("service")]
    public required string Service { get; init; }

    [JsonPropertyName("environment")]
    public required string Environment { get; init; }

    [JsonPropertyName("severity")]
    public required string Severity { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("detectedAt")]
    public required DateTimeOffset DetectedAt { get; init; }
}

public sealed record AnalysisCompletedNotification
{
    [JsonPropertyName("incidentId")]
    public required Guid IncidentId { get; init; }

    [JsonPropertyName("confidence")]
    public decimal? Confidence { get; init; }

    [JsonPropertyName("completedAt")]
    public required DateTimeOffset CompletedAt { get; init; }
}

/// <summary>
/// Pushes newly detected incidents to the organization that owns them.
///
/// The API consumes Kafka purely to fan out. It does no work on these events
/// and commits offsets it will never meaningfully replay - which is why its
/// subscription is a broadcast group starting at "latest". Sharing the
/// detector's group would hand each event to one replica and leave clients on
/// the others in silence; starting at "earliest" would replay the retained
/// history into the dashboard as though it had all just happened.
/// </summary>
public sealed class IncidentDetectedFanout(
    IHubContext<IncidentHub> hub,
    ILogger<IncidentDetectedFanout> logger) : IEventHandler<IncidentDetected>
{
    public async Task HandleAsync(
        EventEnvelope<IncidentDetected> envelope,
        EventContext context,
        CancellationToken cancellationToken)
    {
        var payload = envelope.Payload;

        await hub.Clients
            .Group(RealtimeEvents.GroupFor(envelope.TenantId))
            .SendAsync(
                RealtimeEvents.IncidentDetected,
                new IncidentDetectedNotification
                {
                    IncidentId = payload.IncidentId,
                    Service = payload.Service,
                    Environment = payload.Environment,
                    Severity = payload.Severity,
                    Title = payload.Title,
                    DetectedAt = payload.FirstSeenAt
                },
                cancellationToken);

        logger.LogDebug(
            "Pushed incident {IncidentId} to organization {TenantId}.",
            payload.IncidentId, envelope.TenantId);
    }
}

/// <summary>
/// Pushes a completed AI analysis, which is the update people actually wait for
/// - an incident page open on "analysing…" is the one screen in the product
/// where polling was most obviously wrong.
/// </summary>
public sealed class AnalysisCompletedFanout(
    IHubContext<IncidentHub> hub,
    ILogger<AnalysisCompletedFanout> logger) : IEventHandler<IncidentAnalysisCompleted>
{
    public async Task HandleAsync(
        EventEnvelope<IncidentAnalysisCompleted> envelope,
        EventContext context,
        CancellationToken cancellationToken)
    {
        var payload = envelope.Payload;

        await hub.Clients
            .Group(RealtimeEvents.GroupFor(envelope.TenantId))
            .SendAsync(
                RealtimeEvents.AnalysisCompleted,
                new AnalysisCompletedNotification
                {
                    IncidentId = payload.IncidentId,
                    Confidence = payload.Confidence,
                    CompletedAt = payload.CompletedAt
                },
                cancellationToken);

        logger.LogDebug(
            "Pushed analysis for incident {IncidentId} to organization {TenantId}.",
            payload.IncidentId, envelope.TenantId);
    }
}
