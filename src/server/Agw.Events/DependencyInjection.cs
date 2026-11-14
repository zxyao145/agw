using Agw.Events.Application;
using Agw.Events.Application.Delivery;
using Agw.Events.Application.Jobs;
using Agw.Events.Application.Persistence;
using Agw.Events.Application.Sources;
using Agw.Events.Contracts;
using Agw.Events.Infrastructure.Delivery;
using Agw.Events.Infrastructure.Dispatch;
using Agw.Events.Infrastructure.Scheduling;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Events;

public static class DependencyInjection
{
    public static IServiceCollection AddEvents(this IServiceCollection services, bool runWorkers = true)
    {
        services.AddScoped<EventCaller>();
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
        if (runWorkers)
        {
            services.AddHostedService<EventDispatcherHostedService>();
            services.AddHostedService<EventScheduleHostedService>();
            services.AddHostedService<EventDeliveryHostedService>();
        }
        return services;
    }
}
