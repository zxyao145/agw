using Agw.Notifications.Application;
using Agw.Notifications.Application.Tools;
using Agw.Notifications.Configuration;
using Agw.Notifications.Contracts;
using Agw.Notifications.Infrastructure.Dispatch;
using Agw.Notifications.Infrastructure.Stream;
using Agw.Tools.Abstractions.Generated;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Notifications;

public static class DependencyInjection
{
    public static IServiceCollection AddNotifications(
        this IServiceCollection services,
        IConfiguration configuration,
        bool runWorkers = true
    )
    {
        services.Configure<NotificationDispatchOptions>(configuration.GetSection("Notifications:Dispatch"));
        services.Configure<NotificationStreamOptions>(configuration.GetSection("Notifications:Stream"));
        services.AddSingleton<NotificationStreamHub>();
        if (runWorkers)
        {
            services.AddHostedService<NotificationStreamHostedService>();
            services.AddHostedService<NotificationDispatchHostedService>();
        }
        services.AddScoped<NotificationChannelCatalog>();
        services.AddScoped<NotificationAppService>();
        services.AddScoped<NotificationDispatchAppService>();
        services.AddScoped<INotificationFacade, NotificationFacade>();
        services.AddScoped<INotificationStaging, NotificationStagingFacade>();
        services.AddScoped<INotificationAnswerFacade, NotificationAnswerFacade>();
        services.AddScoped<NotificationToolExecutor>();
        services.AddSingleton<IAgwGeneratedToolModule>(Agw.Generated.Agw.Notifications.AgwToolModule.Instance);
        services.AddSingleton(new AgwToolCatalogType(typeof(NotificationToolExecutor)));
        return services;
    }
}
