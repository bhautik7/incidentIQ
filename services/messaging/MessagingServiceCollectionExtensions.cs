using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
    /// Registers a consumer for one topic and the handler that services it.
    ///
    /// The handler is scoped: each message gets a fresh scope, so it can take a
    /// DbContext and a tenant context exactly the way a web request does.
    /// </summary>
    public static IServiceCollection AddIncidentIQKafkaConsumer<TPayload, THandler>(
        this IServiceCollection services,
        string topic,
        string consumerGroup,
        string? deadLetterTopic = null)
        where THandler : class, IEventHandler<TPayload>
    {
        services.AddScoped<THandler>();

        services.AddSingleton(new KafkaConsumerSubscription<TPayload>
        {
            Topic = topic,
            ConsumerGroup = consumerGroup,
            DeadLetterTopic = deadLetterTopic
        });

        services.AddHostedService<KafkaConsumerService<TPayload, THandler>>();

        return services;
    }
}
