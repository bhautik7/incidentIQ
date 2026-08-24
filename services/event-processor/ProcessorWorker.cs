namespace IncidentIQ.EventProcessor;

/// <summary>
/// Host for the Kafka consume loop. Phase 2 only establishes the shape and the
/// shutdown contract: the consumer, normalisation, fingerprinting and incident
/// correlation arrive in Phase 3.
/// </summary>
internal sealed class ProcessorWorker(ILogger<ProcessorWorker> logger) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation(
            "Event processor worker started. No Kafka consumer is wired up yet (Phase 2 foundation).");

        // Idle until shutdown. Returning immediately would be indistinguishable
        // from a crashed worker in the host logs.
        return Task.Delay(Timeout.Infinite, stoppingToken)
            .ContinueWith(_ => logger.LogInformation("Event processor worker stopping."),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnCanceled,
                TaskScheduler.Default);
    }
}
