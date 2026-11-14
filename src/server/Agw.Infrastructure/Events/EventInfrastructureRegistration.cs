using Agw.Agents.Application.Persistence;
using Agw.Events.Application.Persistence;
using Agw.Events.Configuration;
using Agw.Events.Contracts;
using Agw.Events.Contracts.Transport;
using Agw.Extensions.Contracts;
using Agw.Infrastructure.Agents;
using Agw.Infrastructure.Data;
using Agw.Infrastructure.Events;
using Agw.Infrastructure.Events.Sources;
using Agw.Infrastructure.Events.Transport;
using Agw.Infrastructure.Notifications;
using Agw.Notifications.Application.Persistence;
using Agw.Shared.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Agw.Infrastructure;

public static partial class DependencyInjection
{
    private static void AddEventInfrastructure(IServiceCollection services, IConfiguration configuration)
    {
        services
            .AddOptions<EventsOptions>()
            .Bind(configuration.GetSection(EventsOptions.SectionName))
            .Validate(ValidEventOptions, "Events configuration is invalid.")
            .ValidateOnStart();
        services.AddScoped<IEventsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
        services.AddScoped<INotificationsDbContext>(provider => provider.GetRequiredService<AgwDbContext>());
        services.AddScoped<PendingTransportMessages>();
        services.AddScoped<EventCommitCoordinator>();
        services.AddScoped<EventSaveChangesInterceptor>();
        services.AddScoped<EventTransactionInterceptor>();
        services.AddScoped<EventVersionInterceptor>();
        services.AddScoped<IOutboxStaging, EventOutboxStaging>();
        services.AddScoped<EventRecordRecovery>();
        services.AddScoped<IEventRecoveryControl, EventRecoveryControl>();
        services.AddScoped<EventStaging>();
        services.AddScoped<IEventStaging>(provider => provider.GetRequiredService<EventStaging>());
        services.AddScoped<IEventSourceFacade>(provider => provider.GetRequiredService<EventStaging>());
        services.AddScoped<IEventContextFacade>(provider => provider.GetRequiredService<EventStaging>());
        services.AddScoped<IEventTemplateValidator, EventTemplateValidator>();
        services.AddScoped<IEventIngestTransaction, EventIngestTransaction>();
        services.AddScoped<IEventJobProjectLock, EventJobProjectLock>();
        services.AddScoped<IEventScheduledPublisher, EventScheduledPublisher>();
        services.AddScoped<IEventDispatchNoticeStaging, EventDispatchNoticeStaging>();
        services.AddScoped<IBackgroundExecutionPersistence, BackgroundExecutionPersistence>();
        services.AddScoped<IBackgroundExecutionCompletionTransaction, BackgroundExecutionCompletionTransaction>();
        services.AddScoped<INotificationTransaction, NotificationTransaction>();
        services.AddScoped<INotificationDispatchStore, NotificationDispatchStore>();
        services.AddSingleton(provider => new CloudEventEnvelopeSerializer(
            provider.GetRequiredService<IOptions<EventsOptions>>().Value.Payload.MaxBytes
        ));
        services.AddSingleton<ICloudEventEnvelopeSerializer>(provider =>
            provider.GetRequiredService<CloudEventEnvelopeSerializer>()
        );
        services.AddSingleton<EventRedisConnection>();
        services.AddSingleton<IEventTransport>(CreateEventTransport);
        services.AddEventSourcesInfrastructure();
    }

    public static IServiceCollection AddEventWorkers(this IServiceCollection services)
    {
        services.AddHostedService<EventRetryRecoveryHostedService>();
        services.AddHostedService<EventRecoveryHostedService>();
        services.AddHostedService<EventSourceScanHostedService>();
        return services;
    }

    private static IEventTransport CreateEventTransport(IServiceProvider provider)
    {
        var options = provider.GetRequiredService<IOptions<EventsOptions>>().Value;
        var serializer = provider.GetRequiredService<CloudEventEnvelopeSerializer>();
        if (options.Transport.Provider.Equals("InProcess", StringComparison.OrdinalIgnoreCase))
            return new InProcessEventTransport(serializer);
        using var scope = provider.CreateScope();
        var serverId = scope
            .ServiceProvider.GetRequiredService<IExtensionDeploymentIdentity>()
            .GetServerIdAsync()
            .GetAwaiter()
            .GetResult();
        return new RedisEventTransport(
            provider.GetRequiredService<EventRedisConnection>().Connection,
            serializer,
            serverId,
            provider.GetRequiredService<ILogger<RedisEventTransport>>(),
            options.Transport.Redis.StreamMaxLength
        );
    }

    private static bool ValidEventOptions(EventsOptions options) =>
        (
            string.Equals(options.Transport.Provider, "InProcess", StringComparison.OrdinalIgnoreCase)
            || string.Equals(options.Transport.Provider, "Redis", StringComparison.OrdinalIgnoreCase)
        )
        && (
            !string.Equals(options.Transport.Provider, "Redis", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(options.Transport.Redis.ConnectionString)
        )
        && options.Transport.Redis.StreamMaxLength > 0
        && options.Payload.MaxBytes > 0
        && options.Outbox.StaleAfter > TimeSpan.Zero
        && options.Outbox.RecoveryInterval > TimeSpan.Zero
        && options.Dispatch.MaxHops >= 0
        && options.Delivery.MaxAttempts > 0
        && options.Delivery.BaseBackoff > TimeSpan.Zero
        && options.Delivery.MaxBackoff >= options.Delivery.BaseBackoff
        && options.Delivery.RunningTimeout > TimeSpan.Zero
        && options.HttpPoll.MinInterval > TimeSpan.Zero
        && options.HttpPoll.MaxPages > 0
        && options.HttpPoll.MaxItems > 0
        && options.HttpPoll.MaxResponseBytes > 0;

    private sealed class EventRedisConnection : IDisposable
    {
        public IConnectionMultiplexer Connection { get; }

        public EventRedisConnection(IOptions<EventsOptions> options)
        {
            var connection = options.Value.Transport.Redis.ConnectionString;
            if (string.IsNullOrWhiteSpace(connection))
                throw new AgwException(ErrorCodes.EventTransportUnavailable, "Events Redis connection is missing.");
            var configuration = ConfigurationOptions.Parse(connection);
            configuration.AbortOnConnectFail = false;
            Connection = ConnectionMultiplexer.Connect(configuration);
        }

        public void Dispose() => Connection.Dispose();
    }
}
