using Agw.Setup.Contracts;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agw.Setup.Services;

public static class DependencyInjection
{
    public static IServiceCollection AddSetup(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<SystemInitializationSettings>(configuration.GetSection(SystemInitializationSettings.SectionName));

        services
            .AddSingleton<IInitializationStateStore, JsonInitializationStateStore>()
            .AddScoped<ISetupInitializationService, SetupInitializationService>();

        return services;
    }
}
