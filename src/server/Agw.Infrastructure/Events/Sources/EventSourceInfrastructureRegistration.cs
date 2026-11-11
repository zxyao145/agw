using Agw.Events.Application.Persistence;
using Agw.Events.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Agw.Infrastructure.Events.Sources;

public static class EventSourceInfrastructureRegistration
{
    public static IServiceCollection AddEventSourcesInfrastructure(this IServiceCollection services)
    {
        services
            .AddHttpClient(EventHttpPoller.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {
                var options = provider.GetRequiredService<IOptions<EventsOptions>>().Value.HttpPoll;
                var handler = EventHttpPoller.CreateHandler(options);
                handler.UseCookies = false;
                return handler;
            });
        services.AddScoped<EventHttpPoller>();
        services.AddScoped<ExtensionEventSourcePoller>();
        services.AddScoped<IEventSourcePollService, EventSourcePollService>();
        services.AddScoped<EventSourceScanWorker>();
        services.AddScoped<IEventSourceScanService, EventSourceScanService>();
        services.AddScoped<IEventWebhookReceiver, EventWebhookReceiver>();
        return services;
    }
}
