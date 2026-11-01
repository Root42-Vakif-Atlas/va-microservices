using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using VakifAtlas.Contracts;
using VakifAtlas.ServiceDefaults;

namespace VakifAtlas.Messaging;

/// <summary>Registers event subscriptions for the service's consumer.</summary>
public sealed class MessagingBuilder
{
    internal MessagingBuilder(IServiceCollection services, SubscriptionRegistry registry)
    {
        Services = services;
        Registry = registry;
    }

    /// <summary>Service collection the handlers are registered in.</summary>
    public IServiceCollection Services { get; }

    internal SubscriptionRegistry Registry { get; }

    /// <summary>Binds the service queue to the event and registers its scoped handler.</summary>
    /// <typeparam name="TEvent">Event type to subscribe to.</typeparam>
    /// <typeparam name="THandler">Handler that processes the event.</typeparam>
    public MessagingBuilder Subscribe<TEvent, THandler>()
        where TEvent : class, IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        Services.TryAddScoped<THandler>();
        Registry.Subscriptions.Add(new Subscription(EventNames.Of<TEvent>(), typeof(TEvent), typeof(THandler)));
        return this;
    }
}

/// <summary>Registers the outbox, inbox and RabbitMQ messaging services.</summary>
public static class MessagingExtensions
{
    /// <summary>Registers the outbox publisher and the consumer; the DbContext must call <see cref="MessagingModelBuilderExtensions.AddMessagingTables"/> and <see cref="AddOutboxInterceptors"/>.</summary>
    /// <typeparam name="TDbContext">DbContext that holds the outbox and inbox tables.</typeparam>
    /// <param name="builder">Web application builder to configure.</param>
    /// <param name="queue">Service queue name, also used as the audit service name.</param>
    public static MessagingBuilder AddMessaging<TDbContext>(this WebApplicationBuilder builder, string queue)
        where TDbContext : DbContext
    {
        IConfigurationSection section = builder.Configuration.GetSection(MessagingOptions.Section);
        builder.Services.Configure<MessagingOptions>(section);
        var enabled = section.GetValue(nameof(MessagingOptions.Enabled), defaultValue: true);

        builder.Services.AddSingleton<OutboxSignal>();
        builder.Services.AddSingleton<OutboxSignalInterceptor>();
        builder.Services.AddScoped<IEventPublisher, OutboxEventPublisher<TDbContext>>();
        builder.Services.AddScoped(services => new StaffAudit(
            services.GetRequiredService<IEventPublisher>(),
            services.GetRequiredService<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),
            services.GetRequiredService<TimeProvider>(),
            queue));

        var registry = new SubscriptionRegistry(queue);
        builder.Services.AddSingleton(registry);

        if (enabled)
        {
            builder.Services.AddSingleton<RabbitMqConnection>();
            builder.Services.AddHostedService<OutboxDispatcher<TDbContext>>();
            builder.Services.AddHostedService<EventConsumerService<TDbContext>>();
            builder.Services.AddHealthChecks().AddCheck<RabbitMqHealthCheck>("rabbitmq", HealthStatus.Degraded, tags: [ServiceDefaultsExtensions.ReadyTag]);
        }

        return new MessagingBuilder(builder.Services, registry);
    }

    /// <summary>Adds the interceptor that wakes the outbox dispatcher after saves and commits.</summary>
    /// <param name="options">DbContext options to configure.</param>
    /// <param name="services">Service provider that resolves the interceptor.</param>
    public static DbContextOptionsBuilder AddOutboxInterceptors(this DbContextOptionsBuilder options, IServiceProvider services) =>
        options.AddInterceptors((IInterceptor)services.GetRequiredService<OutboxSignalInterceptor>());
}
