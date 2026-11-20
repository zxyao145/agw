using Agw.Events.Application;
using Agw.Events.Application.Delivery;
using Agw.Events.Application.Jobs;
using Agw.Events.Application.Persistence;
using Agw.Events.Application.Sources;
using Agw.Events.Contracts;
using Agw.Events.Domain.Repositories;
using Agw.Events.Domain.Services;
using Agw.Events.Infrastructure;
using Agw.Events.Infrastructure.Delivery;
using Agw.Events.Infrastructure.Dispatch;
using Agw.Events.Infrastructure.Scheduling;
using Agw.Events.Infrastructure.Sources;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Events;

public static class DependencyInjection
{
    public static IServiceCollection AddEvents(this IServiceCollection services, bool runWorkers = true)
    {
        services.AddScoped<EventCaller>();
        services.AddScoped<IEventSourceNameRepository, EventSourceNameRepository>();
        services.AddScoped<EventSourceNameDomainService>();
        services.AddScoped<IEventPublisherSources, EventPublisherSources>();
        services.AddScoped<IEventSourceFacade, EventSourceFacade>();
        services.AddScoped<IEventContextFacade, EventContextFacade>();
        services.AddScoped<EventStaging>();
        services.AddScoped<IEventStaging>(provider => provider.GetRequiredService<EventStaging>());
        services.AddScoped<IEventScheduledPublisher, EventScheduledPublisher>();
        services.AddScoped<IEventPublisher, EventPublisher>();
        services.AddScoped<IEventScheduler, EventScheduler>();
        services.AddScoped<IEventSubscriptionFacade, EventSubscriptionFacade>();
        services.AddScoped<EventDispatcher>();
        services.AddScoped<EventScheduleRunner>();
        services.AddScoped<EventManagementAppService>();
        services.AddScoped<EventSourceAppService>();
        services.AddScoped<EventTimerScheduleSync>();
        services.AddScoped<IEventJobFacade, EventJobFacade>();
        services.AddScoped<EventDeliveryProcessor>();
        services.AddScoped<EventDeliveryManagementAppService>();
        services.AddScoped<IEventExecutionCompletionHandler, EventExecutionCompletionHandler>();
        services.AddEventSourcesInfrastructure();
        if (runWorkers)
        {
            services.AddHostedService<EventDispatcherHostedService>();
            services.AddHostedService<EventScheduleHostedService>();
            services.AddHostedService<EventDeliveryHostedService>();
            services.AddHostedService<EventSourceScanHostedService>();
        }
        return services;
    }
}
