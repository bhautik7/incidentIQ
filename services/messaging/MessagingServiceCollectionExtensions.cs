using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace IncidentIQ.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared producer. Singleton, because a Kafka producer is
    /// thread-safe and its batching and idempotence state must be process-wide.
    /// </summary>
    public static IServiceCollection AddIncidentIQKafkaProducer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<KafkaOptions>(configuration.GetSection(KafkaOptions.SectionName));
        // Registered once, exposed under both types. A second AddSingleton with
        // a factory would make the container treat the instance as owned twice
        // and dispose it twice.
        services.AddSingleton<IEventProducer, KafkaEventProducer>();
        services.AddSingleton(sp => (KafkaEventProducer)sp.GetRequiredService<IEventProducer>());

        return services;
    }

    /// <summary>
    /// The registry every consume loop reports into, plus the readiness check
    /// that reads it.
    ///
    /// Registered by both consumer extensions so a host cannot end up with
    /// consumers and no way to tell whether they are alive - which is the
    /// situation this whole mechanism exists to prevent.
    /// </summary>
    private static void AddConsumerLiveness(IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ConsumerLivenessRegistry>();
    }

    /// <summary>
    /// Registers a consumer for one topic and the handler that services it.
    ///
    /// The handler is scoped: each message gets a fresh scope, so it can take a
    /// DbContext and a tenant context exactly the way a web request does.
    /// </summary>
    public static IServiceCollection AddIncidentIQKafkaConsumer<TPayload, THandler>(
        this IServiceCollection services,
        string topic,
        string consumerGroup,
        string? deadLetterTopic = null,
        bool broadcastToEveryInstance = false,
        string? autoOffsetReset = null)
        where THandler : class, IEventHandler<TPayload>
    {
        AddConsumerLiveness(services);
        services.AddScoped<THandler>();

        services.AddSingleton(new KafkaConsumerSubscription<TPayload>
        {
            Topic = topic,
            ConsumerGroup = consumerGroup,
            DeadLetterTopic = deadLetterTopic,
            BroadcastToEveryInstance = broadcastToEveryInstance,
            AutoOffsetResetOverride = autoOffsetReset
        });

        services.AddHostedService<KafkaConsumerService<TPayload, THandler>>();

        return services;
    }

    /// <summary>
    /// Registers a batching consumer for one topic.
    ///
    /// Use this instead of the per-message variant whenever the handler talks
    /// to a database: batching is what turns one round trip per log line into
    /// one per batch.
    /// </summary>
    public static IServiceCollection AddIncidentIQKafkaBatchConsumer<TPayload, THandler>(
        this IServiceCollection services,
        string topic,
        string consumerGroup,
        string? deadLetterTopic = null,
        int maxBatchSize = 500,
        int maxBatchWaitMs = 250)
        where THandler : class, IEventBatchHandler<TPayload>
    {
        AddConsumerLiveness(services);
        services.AddScoped<THandler>();

        services.AddSingleton(new KafkaBatchConsumerSubscription<TPayload>
        {
            Topic = topic,
            ConsumerGroup = consumerGroup,
            DeadLetterTopic = deadLetterTopic,
            MaxBatchSize = maxBatchSize,
            MaxBatchWaitMs = maxBatchWaitMs
        });

        services.AddHostedService<KafkaBatchConsumerService<TPayload, THandler>>();

        return services;
    }
}
